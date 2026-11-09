using System.Diagnostics;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Domain.Wallpapers;
using LiveWall.Host.Bootstrap;
using LiveWall.Host.CommandLoop;
using LiveWall.Infrastructure.Configuration;

namespace LiveWall.Host.Orchestration;

public sealed record HostCrashRecoveryRequest(
    string RunDirectory,
    string SourceVideoPath,
    int Crashes,
    TimeSpan ObservationDuration,
    Action<string>? Progress = null);

/// <param name="Downtime">From killing the Renderer to the restarted session's commit, or to the open circuit.</param>
public sealed record HostCrashRecoveryCrash(
    int Crash,
    int KilledProcessId,
    int? KilledExitCode,
    RendererRecoveryOutcome Outcome,
    TimeSpan? ScheduledDelay,
    TimeSpan Downtime,
    int? RestartedProcessId);

public sealed record HostCrashRecoveryResult(
    WallpaperImportResult Import,
    ApplyGenerationResult? Initial,
    IReadOnlyList<HostCrashRecoveryCrash> Crashes,
    IReadOnlyList<RendererRecoveryEntry> Journal,
    SessionRetirementBatchResult Retirement,
    IReadOnlyList<int?> FinalRendererExitCodes,
    IReadOnlyList<WallpaperAssignment> PersistedAssignments,
    string? Failure)
{
    public bool Valid => Failure is null;
}

/// <summary>
/// ARCHITECTURE §11.5 with a real Renderer: import a local video into a fresh data root, apply it, then kill
/// the Renderer process up to four times. The first three crashes must come back after 0, 2 and 8 seconds with
/// a fresh process; a fourth inside 60 seconds must open the circuit and keep the Assignment. The Host itself
/// must stay healthy throughout.
/// </summary>
public static class HostCrashRecoveryDiagnostics
{
    private static readonly TimeSpan[] ExpectedDelays = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8)];
    private static readonly TimeSpan RecoveryBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumObservation = TimeSpan.FromSeconds(30);
    private const int ProcessExitBudgetMilliseconds = 3000;

    /// <summary>Real desktop composition; only the unvalidated Raised attachment is overridden.</summary>
    public static Task<HostCrashRecoveryResult> RunWithRaisedDiagnosticsAsync(
        HostCrashRecoveryRequest request,
        string rendererDirectory,
        CancellationToken cancellationToken)
    {
        _ = HostContentRestoreDiagnostics.RequireVideoProviders(rendererDirectory);
        return RunAsync(
            request,
            (configuration, repository) =>
            {
                WindowsHostComposition composition = WindowsHostCompositionRoot.CreateComposition(
                    configuration,
                    new WindowsHostCompositionOptions(
                        repository,
                        HostContentRestoreDiagnostics.RequireVideoProviders(rendererDirectory),
                        DesktopAttachmentPolicy.AllowUnvalidatedRaisedDiagnostics));
                return new HostRuntimeHandle(composition.Runtime, composition.CommandLoop);
            },
            cancellationToken);
    }

    internal static async Task<HostCrashRecoveryResult> RunAsync(
        HostCrashRecoveryRequest request,
        HostRuntimeFactory createRuntime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(createRuntime);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Crashes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Crashes, ExpectedDelays.Length + 1);
        if (request.ObservationDuration <= TimeSpan.Zero || request.ObservationDuration > MaximumObservation)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Observation must be positive and at most 30 seconds.");
        }

        (string configuration, WallpaperImportResult imported, string? contentRoot) =
            await HostContentRestoreDiagnostics.ImportIntoFreshDataRootAsync(
                    request.RunDirectory, request.SourceVideoPath, cancellationToken)
                .ConfigureAwait(false);
        if (imported.WallpaperId is not { } wallpaperId || contentRoot is null)
        {
            return new HostCrashRecoveryResult(imported, null, [], [], new SessionRetirementBatchResult([]), [], [],
                HostContentRestoreDiagnostics.ImportFailure(imported));
        }

        HostRuntimeHandle host = createRuntime(configuration, WindowsHostCompositionRoot.CreateWallpaperRepository(configuration));
        ApplyGenerationResult? initial = null;
        List<HostCrashRecoveryCrash> crashes = [];
        List<Process> survivors = [];
        DisplayId? display = null;
        string? failure = null;
        try
        {
            await host.Runtime.StartAsync(cancellationToken).ConfigureAwait(false);
            HostStateSnapshot state = await HostContentRestoreDiagnostics.GetStateAsync(host.CommandLoop, cancellationToken)
                .ConfigureAwait(false);
            display = state.DisplayTopology?.Displays.FirstOrDefault(item => item.IsPrimary)?.Id ??
                throw new InvalidOperationException("No primary display is connected.");
            HostCommandResult accepted = await host.CommandLoop.SendAsync(
                    new ApplyWallpaperCommand(wallpaperId, [display.Value], LayoutMode.PerDisplay, FitMode.Cover),
                    cancellationToken)
                .ConfigureAwait(false);
            long generation = accepted is { Accepted: true, State: { } applyState }
                ? applyState.Generation
                : throw new InvalidOperationException($"Apply was rejected: {accepted.ErrorCode}.");
            initial = await host.CommandLoop.WaitForApplyGenerationAsync(generation, cancellationToken).ConfigureAwait(false);
            if (initial.State != ApplyGenerationTerminalState.Succeeded)
            {
                throw new InvalidOperationException($"The initial Apply ended as {initial.State}.");
            }

            request.Progress?.Invoke($"Applied; observe for {request.ObservationDuration.TotalSeconds:0} seconds.");
            await Task.Delay(request.ObservationDuration, cancellationToken).ConfigureAwait(false);
            for (int crash = 1; crash <= request.Crashes; crash++)
            {
                crashes.Add(await CrashOnceAsync(host.CommandLoop, crash, request, cancellationToken).ConfigureAwait(false));
            }

            foreach (PreparedSession item in host.CommandLoop.GetPreparedSessionsForDiagnostics())
            {
                if (item.Renderer is RendererProtocolSession protocol)
                {
                    Process process = Process.GetProcessById(protocol.ProcessId);
                    _ = process.SafeHandle; // Hold this identity; a reused PID must never be reopened later.
                    survivors.Add(process);
                }
            }

            failure = VerifyCrashes(crashes);
        }
        catch (Exception exception)
        {
            failure = $"{exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            await host.Runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (failure is null && host.Runtime.LastFault is { } fault)
        {
            failure = $"A handled Renderer crash faulted the Host: {fault.GetType().Name}: {fault.Message}";
        }

        SessionRetirementBatchResult retirement = new(Array.AsReadOnly(
            host.CommandLoop.GetRetirementHistoryForDiagnostics()
                .SelectMany(batch => batch.Sessions)
                .GroupBy(item => item.SessionId)
                .Select(group => group.Last())
                .ToArray()));
        List<int?> exitCodes = [];
        foreach (Process process in survivors)
        {
            using (process)
            {
                exitCodes.Add(process.WaitForExit(ProcessExitBudgetMilliseconds) ? process.ExitCode : null);
            }
        }

        IReadOnlyList<WallpaperAssignment> persisted = (await new JsonHostConfigurationStore(configuration)
                .LoadAsync(CancellationToken.None)
                .ConfigureAwait(false))
            .Configuration.Assignments;
        if (failure is null && exitCodes.Any(code => code != 0))
        {
            failure = "The surviving Renderer did not exit with code 0.";
        }

        if (failure is null && !(persisted is [{ } assignment] &&
            assignment.WallpaperId == wallpaperId && assignment.DisplayId == display))
        {
            failure = "The persisted assignment does not name the imported wallpaper on its display.";
        }

        return new HostCrashRecoveryResult(imported, initial, crashes, host.CommandLoop.GetRendererRecoveryJournalForDiagnostics(),
            retirement, exitCodes, persisted, failure);
    }

    private static async Task<HostCrashRecoveryCrash> CrashOnceAsync(
        HostCommandLoop loop,
        int crash,
        HostCrashRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        PreparedSession active = loop.GetPreparedSessionsForDiagnostics() is [{ } single]
            ? single
            : throw new InvalidOperationException($"Crash {crash} expected exactly one active session.");
        int killedProcessId = ((RendererProtocolSession)active.Renderer).ProcessId;
        IReadOnlyList<RendererRecoveryEntry> earlier = loop.GetRendererRecoveryJournalForDiagnostics();
        long mark = earlier.Count == 0 ? 0 : earlier[^1].Sequence;
        int? killedExitCode;
        Stopwatch downtime = Stopwatch.StartNew();
        using (Process process = Process.GetProcessById(killedProcessId))
        {
            _ = process.SafeHandle; // Hold the handle before the kill so the real exit code stays readable.
            process.Kill(entireProcessTree: true);
            killedExitCode = process.WaitForExit(ProcessExitBudgetMilliseconds) ? process.ExitCode : null;
        }

        RendererRecoveryEntry terminal = await WaitForJournalAsync(loop, mark,
                entry => entry.Outcome is RendererRecoveryOutcome.Restarted or RendererRecoveryOutcome.CircuitBroken,
                cancellationToken)
            .ConfigureAwait(false);
        downtime.Stop();
        RendererRecoveryEntry? decision = loop.GetRendererRecoveryJournalForDiagnostics().FirstOrDefault(entry =>
            entry.Sequence > mark &&
            entry.Outcome is RendererRecoveryOutcome.RestartScheduled or RendererRecoveryOutcome.CircuitBroken);
        int? restartedProcessId = null;
        if (terminal.Outcome == RendererRecoveryOutcome.Restarted)
        {
            restartedProcessId = loop.GetPreparedSessionsForDiagnostics() is [{ Renderer: RendererProtocolSession restarted }]
                ? restarted.ProcessId
                : null;
            request.Progress?.Invoke($"Crash {crash}: restarted after {downtime.Elapsed.TotalSeconds:0.0} s; " +
                $"observe for {request.ObservationDuration.TotalSeconds:0} seconds.");
            await Task.Delay(request.ObservationDuration, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            request.Progress?.Invoke($"Crash {crash}: the circuit is open; the original wallpaper stays.");
        }

        return new HostCrashRecoveryCrash(crash, killedProcessId, killedExitCode, terminal.Outcome,
            decision?.Delay, downtime.Elapsed, restartedProcessId);
    }

    private static string? VerifyCrashes(IReadOnlyList<HostCrashRecoveryCrash> crashes)
    {
        foreach (HostCrashRecoveryCrash crash in crashes)
        {
            int index = crash.Crash - 1;
            bool restart = index < ExpectedDelays.Length;
            if (crash.KilledExitCode is null)
            {
                return $"Crash {crash.Crash}: the killed Renderer did not exit.";
            }

            if (!restart)
            {
                if (crash.Outcome != RendererRecoveryOutcome.CircuitBroken)
                {
                    return $"Crash {crash.Crash} ended as {crash.Outcome}; the circuit should have opened.";
                }

                continue;
            }

            if (crash.Outcome != RendererRecoveryOutcome.Restarted || crash.ScheduledDelay != ExpectedDelays[index])
            {
                return $"Crash {crash.Crash} ended as {crash.Outcome} after a {crash.ScheduledDelay} back-off; " +
                    $"expected a restart after {ExpectedDelays[index]}.";
            }

            if (crash.Downtime < ExpectedDelays[index] ||
                crash.RestartedProcessId is null || crash.RestartedProcessId == crash.KilledProcessId)
            {
                return $"Crash {crash.Crash} did not come back in a fresh process after its back-off.";
            }
        }

        return null;
    }

    private static async Task<RendererRecoveryEntry> WaitForJournalAsync(
        HostCommandLoop loop,
        long after,
        Func<RendererRecoveryEntry, bool> predicate,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(RecoveryBudget);
        try
        {
            while (true)
            {
                if (loop.GetRendererRecoveryJournalForDiagnostics()
                        .FirstOrDefault(entry => entry.Sequence > after && predicate(entry)) is { } found)
                {
                    return found;
                }

                await Task.Delay(25, budget.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Renderer recovery did not finish within {RecoveryBudget.TotalSeconds:0} seconds.");
        }
    }
}
