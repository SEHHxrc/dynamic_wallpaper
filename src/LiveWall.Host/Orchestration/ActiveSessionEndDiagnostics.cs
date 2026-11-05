using System.Text.Json;
using System.Text.Json.Serialization;
using LiveWall.Domain.Displays;
using LiveWall.Host.Bootstrap;
using LiveWall.Infrastructure.Configuration;
using LiveWall.Infrastructure.FileSystem;
using LiveWall.Platform.Windows.Lifecycle;

namespace LiveWall.Host.Orchestration;

public sealed record ActiveSessionEndEvidence(int SchemaVersion, string RunId, DateTimeOffset RecordedAt,
    long Generation, IReadOnlyList<string> ExpectedSessionIds, bool InitialCommitSucceeded,
    int OwnedSurfaceCount, IReadOnlyList<SessionRetirementResult> Retirement,
    IReadOnlyList<RendererCleanupEvidence> RendererCleanup)
{
    // Validate identities, not just counts: empty and duplicate evidence must fail closed.
    public bool Valid => SchemaVersion == 1 && Generation > 0 && InitialCommitSucceeded && OwnedSurfaceCount == 0 &&
        ExpectedSessionIds.Count > 0 && ExpectedSessionIds.All(id => !string.IsNullOrWhiteSpace(id)) &&
        ExpectedSessionIds.Distinct(StringComparer.Ordinal).Count() == ExpectedSessionIds.Count &&
        Retirement.Count == ExpectedSessionIds.Count && RendererCleanup.Count == ExpectedSessionIds.Count &&
        ExpectedSessionIds.All(id => Retirement.Count(item => item.SessionId.Value == id && item.Generation == Generation &&
            item.ShutdownSent && item.ShutdownCompleted && item.OwnedResourcesCleaned && item.Issues.Count == 0) == 1 &&
            RendererCleanup.Count(item => item.SessionId == id && item.ExitObserved && item.HandleDisposed && item.Issue is null) == 1);
}

public static partial class WindowsHostEventChainDiagnostics
{
    private static readonly JsonSerializerOptions SessionEndEvidenceOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // Explicit diagnostic composition only. The production Host accepts no Raised override.
    public static async Task RunActiveSessionEndObserverAsync(string rendererPath, DisplayTopology topology,
        string runDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runDirectory);
        string root = Path.GetFullPath(runDirectory);
        string configurationDirectory = Path.Combine(root, "configuration");
        foreach (string name in new[] { "active-starting.json", "active-ready.json", "active-exit-evidence.json" })
            if (File.Exists(Path.Combine(root, name))) throw new InvalidOperationException("A fresh active session-end run is required.");
        if (File.Exists(Path.Combine(configurationDirectory, "host-exit-receipt.json")) ||
            File.Exists(Path.Combine(configurationDirectory, "host-native-exit-receipt.json")) ||
            File.Exists(Path.Combine(configurationDirectory, "assignments.json")) ||
            File.Exists(Path.Combine(configurationDirectory, "playback-policy.json")))
            throw new InvalidOperationException("A fresh isolated configuration is required; existing files will not be overwritten.");

        using WindowsProcessShutdownOrder shutdownOrder = WindowsProcessShutdownOrder.ForHost();
        DiagnosticRendererProcessTracker tracker = new();
        var composition = CreateComposition(rendererPath, topology, ProductCandidateDisplaySelection.Primary,
            tracker, configurationDirectory).Composition;
        string[] expectedSessions = [];
        long generation = 0;
        bool committed = false;
        using HostProcessLifetime lifetime = new(composition.Runtime,
            receipt => AtomicFileWriter.WriteJsonAsync(Path.Combine(configurationDirectory, "host-exit-receipt.json"), receipt),
            async (native, token) =>
            {
                ActiveSessionEndEvidence evidence = new(1, native.RunId, DateTimeOffset.UtcNow, generation,
                    expectedSessions, committed, composition.DesktopHost.OwnedSurfaceCountForDiagnostics,
                    composition.CommandLoop.GetRetirementHistoryForDiagnostics().SelectMany(batch => batch.Sessions).ToArray(),
                    tracker.Snapshot());
                await AtomicFileWriter.WriteJsonAsync(Path.Combine(root, "active-exit-evidence.json"), evidence,
                    SessionEndEvidenceOptions, token).ConfigureAwait(false);
                await AtomicFileWriter.WriteJsonAsync(Path.Combine(configurationDirectory, "host-native-exit-receipt.json"),
                    native, cancellationToken: token).ConfigureAwait(false);
            });
        await using WindowsProcessExitMonitor monitor = new(lifetime.RequestStop,
            persistObservationAsync: lifetime.PersistNativeObservationAsync);
        try
        {
            await monitor.Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
            await AtomicFileWriter.WriteJsonAsync(Path.Combine(root, "active-starting.json"), new
            {
                schemaVersion = 1, startupBudgetMilliseconds = ApplyDeadlineBudget.Default.Calculate(1).TotalMilliseconds,
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            await composition.Runtime.StartAsync(cancellationToken).ConfigureAwait(false);
            EnsureInitialApplySucceeded(composition.Runtime);
            var initial = await GetStateAsync(composition.CommandLoop, cancellationToken).ConfigureAwait(false);
            expectedSessions = initial.Sessions.Select(item => item.Session.Id.Value).ToArray();
            generation = initial.Generation;
            committed = expectedSessions.Length == 1 && composition.DesktopHost.ReplacementCountForDiagnostics == 1;
            if (!committed) throw new InvalidOperationException("Active session-end requires one committed primary-display session.");
            JsonHostConfigurationStore store = new(configurationDirectory);
            await store.SaveAssignmentsAsync(initial.Assignments, cancellationToken).ConfigureAwait(false);
            await store.SavePlaybackPolicyAsync(initial.PlaybackPolicy, cancellationToken).ConfigureAwait(false);
            await AtomicFileWriter.WriteJsonAsync(Path.Combine(root, "active-ready.json"), new
            {
                schemaVersion = 1, scope = "ActiveRendererSessionEnd", generation,
                expectedSessionIds = expectedSessions, firstFrameCommitted = committed,
                activeSurfaceCount = composition.DesktopHost.OwnedSurfaceCountForDiagnostics,
                hostShutdownLevel = shutdownOrder.Level,
                rendererShutdownLevel = WindowsProcessShutdownOrder.RendererLevel,
                armedAt = DateTimeOffset.UtcNow,
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            monitor.MarkRunning();
            Console.WriteLine("ACTIVE_SESSION_END_ARMED: orange remains until manual sign-out; no automatic OS operation. Maximum human wait: 10 minutes.");
            using CancellationTokenSource humanWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            try { await Task.Delay(TimeSpan.FromMinutes(10), humanWait.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsStopRequested) { }
            if (!lifetime.IsStopRequested) throw new TimeoutException("Manual sign-out was not observed; the diagnostic will clean its owned resources.");
        }
        finally
        {
            await lifetime.RequestStop("DiagnosticObserverEnded").ConfigureAwait(false);
            // Await native persistence before disposing the lifetime used by its callback.
            await monitor.DisposeAsync().ConfigureAwait(false);
        }
    }
}
