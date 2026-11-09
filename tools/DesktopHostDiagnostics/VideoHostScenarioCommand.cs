using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveWall.Application.Importing;
using LiveWall.Diagnostics;
using LiveWall.Host.Orchestration;
using LiveWall.Importers.Native;
using LiveWall.Platform.Windows.Desktop;
using LiveWall.Platform.Windows.Displays;

namespace LiveWall.DesktopHostDiagnostics;

// Visible Host scenarios on the real desktop. Both use the real Host runtime, import pipeline, persistent
// library, JSON configuration store and default Video provider; only the unvalidated Raised attachment is
// overridden. C2.1 restores content across a Host restart; C3 kills the Renderer and expects §11.5 recovery.
internal static class VideoHostScenarioCommand
{
    private const string LibraryVariable = "LIVEWALL_VIDEO_LIBMPV_PATH";
    private static readonly TimeSpan PauseBetweenHosts = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static async Task<int> RunAsync(VideoHostScenarioOptions options)
    {
        if (Directory.Exists(options.RunDirectory) || File.Exists(options.RunDirectory))
            throw new IOException("Use a fresh run directory.");
        bool restore = options.Scenario == VideoHostScenario.Restore;
        string scope = restore ? "C2HostContentRestore" : "C3RendererCrashRecovery";
        string prefix = restore ? "C2_RESTORE" : "C3_CRASH";
        Guid runId = Guid.NewGuid();
        DateTimeOffset started = DateTimeOffset.UtcNow;
        string output = Path.Combine(options.RunDirectory, "result.json");

        // Read-only checks before any Shell request: source signature, Renderer and pinned native library.
        ImportProbeResult source = await new LocalVideoImporter()
            .ProbeAsync(new ImportSource(options.SourceVideo), CancellationToken.None).ConfigureAwait(false);
        bool rendererPresent = File.Exists(options.RendererExecutable) && string.Equals(
            Path.GetFileName(options.RendererExecutable), "LiveWall.Renderer.Video.exe", StringComparison.OrdinalIgnoreCase);
        bool libraryPinned = File.Exists(options.LibraryPath) && string.Equals(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(options.LibraryPath))),
            VideoProductCandidateAssets.LibrarySha256, StringComparison.OrdinalIgnoreCase);
        using WindowsDisplayTopologySource displays = new();
        var primary = displays.Current.Displays.FirstOrDefault(display => display.IsPrimary);
        ShellTopologySnapshot before = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        bool shellReady = before.Availability == ShellTopologyAvailability.Available &&
            before.ExecutionContext.WindowStationName == "WinSta0" && before.ExecutionContext.DesktopName == "Default" &&
            !before.ShellDescendants.Any(window => VideoProductCandidateCommand.IsOwnedClass(window.ClassName));
        bool ready = source.Recognized && rendererPresent && libraryPinned && primary is not null && shellReady;
        var preflight = new { sourceRecognized = source.Recognized, source.DetectedFormat, sourceReasons = source.Warnings,
            rendererPresent, libraryPinned, primaryDisplay = primary?.Id.Value, primary?.Bounds, primary?.ScaleFactor,
            shellReady, ready };
        if (options.PreflightOnly || !ready)
        {
            Directory.CreateDirectory(options.RunDirectory);
            await WriteAsync(output, new { schemaVersion = 1,
                scope = options.PreflightOnly ? scope + "ReadOnlyPreflight" : scope,
                runId, startedAt = started, fullBuild = before.BuildIdentity.FullBuild, context = before.ExecutionContext,
                preflight, classification = ready ? "ReadOnlyPreflightCompleted" : "EnvironmentBlocked",
                shell = VideoProductCandidateCommand.Summary(before), shellRequestSent = false, surfaceCreated = false,
                rendererStarted = false, desktopPresentationValidated = false, productionAttachmentEnabled = false })
                .ConfigureAwait(false);
            Console.WriteLine($"{prefix}_PREFLIGHT: ready={ready}; result={output}");
            return ready ? 0 : 3;
        }

        Console.WriteLine($"{prefix}_READY: display={primary!.Id}; bounds={primary.Bounds}; scale={primary.ScaleFactor}; " +
            $"observe {options.DurationSeconds} s" + (restore ? " per Host." : $"; crashes={options.Crashes}."));
        ShellTopologySnapshot? requested = null;
        object? result = null;
        bool scenarioValid = false;
        string? scenarioFailure = null;
        Exception? failure = null;
        using CancellationTokenSource cancel = new();
        ConsoleCancelEventHandler handler = (_, args) => { args.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        // Inherited by the Renderer, which reads and clears it; production resolves native/libmpv-2.dll beside it.
        Environment.SetEnvironmentVariable(LibraryVariable, options.LibraryPath);
        try
        {
            string rendererDirectory = Path.GetDirectoryName(options.RendererExecutable)!;
            TimeSpan observation = TimeSpan.FromSeconds(options.DurationSeconds);
            if (restore)
            {
                HostContentRestoreResult restored = await HostContentRestoreDiagnostics.RunWithRaisedDiagnosticsAsync(
                    new HostContentRestoreRequest(options.RunDirectory, options.SourceVideo, observation, PauseBetweenHosts,
                        PhaseCommitted: (phase, _) =>
                        {
                            requested ??= WindowsShellTopologyDiagnostics.CaptureSnapshot();
                            Console.WriteLine(phase == HostContentRestorePhase.Applied
                                ? $"C2_RESTORE_COMMITTED: phase=Applied; the imported video covers the primary display for {options.DurationSeconds} seconds."
                                : $"C2_RESTORE_COMMITTED: phase=Restored; a new Host restored the same video for {options.DurationSeconds} seconds.");
                        },
                        HostStopped: phase => Console.WriteLine(phase == HostContentRestorePhase.Applied
                            ? "C2_RESTORE_HOST_STOPPED: phase=Applied; the original wallpaper shows for about 3 seconds before the second Host starts."
                            : "C2_RESTORE_HOST_STOPPED: phase=Restored; the original wallpaper should stay.")),
                    rendererDirectory, cancel.Token).ConfigureAwait(false);
                result = restored;
                scenarioValid = restored.Valid && restored.Phases.All(phase => phase.RendererExitCodes is [0]);
                scenarioFailure = restored.Failure;
            }
            else
            {
                HostCrashRecoveryResult recovered = await HostCrashRecoveryDiagnostics.RunWithRaisedDiagnosticsAsync(
                    new HostCrashRecoveryRequest(options.RunDirectory, options.SourceVideo, options.Crashes, observation,
                        Progress: message =>
                        {
                            requested ??= WindowsShellTopologyDiagnostics.CaptureSnapshot();
                            Console.WriteLine("C3_CRASH: " + message);
                        }),
                    rendererDirectory, cancel.Token).ConfigureAwait(false);
                result = recovered;
                scenarioValid = recovered.Valid;
                scenarioFailure = recovered.Failure;
            }
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            Environment.SetEnvironmentVariable(LibraryVariable, null);
            Console.CancelKeyPress -= handler;
        }

        ShellTopologySnapshot after = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        ShellMutationAssessment mutation = ShellMutationAssessment.Evaluate(before, requested, after, requestAttempted: true);
        bool windowsRemain = after.ShellDescendants.Any(window => VideoProductCandidateCommand.IsOwnedClass(window.ClassName));
        bool valid = failure is null && scenarioValid && !windowsRemain;
        int exitCode = cancel.IsCancellationRequested ? 130 : !valid ? 8 :
            mutation.Recovery is ShellMutationRecovery.CleanupIncomplete or ShellMutationRecovery.Inconclusive ? 6 : 0;
        Directory.CreateDirectory(options.RunDirectory);
        await WriteAsync(output, new { schemaVersion = 1, scope, runId, startedAt = started,
            fullBuild = before.BuildIdentity.FullBuild, context = before.ExecutionContext, preflight,
            observationSeconds = options.DurationSeconds, crashes = restore ? (int?)null : options.Crashes,
            pauseBetweenHostsSeconds = restore ? PauseBetweenHosts.TotalSeconds : (double?)null,
            result, valid, failure = failure is null ? scenarioFailure : $"{failure.GetType().Name}: {failure.Message}",
            ownedWindowsRemain = windowsRemain, shellMutationRecovery = mutation,
            before = VideoProductCandidateCommand.Summary(before),
            afterRequest = requested is null ? null : VideoProductCandidateCommand.Summary(requested),
            after = VideoProductCandidateCommand.Summary(after),
            classification = exitCode switch { 0 => "CompletedAwaitingManual", 6 => "CompletedWithShellRecoveryIssue",
                130 => "Cancelled", _ => "Failed" },
            exitCode, manualPresentation = "NotYetConfirmed", productionAttachmentEnabled = false }).ConfigureAwait(false);
        Console.WriteLine($"{prefix}_RESULT: exit={exitCode}; valid={valid}; shellRecovery={mutation.Recovery}; result={output}");
        return exitCode;
    }

    private static async Task WriteAsync<T>(string output, T value)
    {
        await using FileStream stream = new(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, value, Json, CancellationToken.None).ConfigureAwait(false);
    }
}
