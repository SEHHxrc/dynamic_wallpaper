using LiveWall.Domain.Displays;
using LiveWall.Host.Orchestration;
using LiveWall.Infrastructure.FileSystem;
using LiveWall.Renderer.Video.Mpv;

namespace LiveWall.VideoPresentationProbe;

// ARCHITECTURE §11.5 with the formal Video Renderer: four real crashes in one Host. The first three must come back
// after 0, 2 and 8 seconds in a fresh process; the fourth must open the circuit. Only the desktop is a private
// hidden container, so no Shell is touched and nothing is shown.
internal static class VideoHostCrashRecoveryPreflight
{
    private const string LibraryVariable = "LIVEWALL_VIDEO_LIBMPV_PATH";

    public static int Run(string renderer, string library, string sourceVideo, string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || Directory.Exists(directory) || File.Exists(directory))
            throw new ArgumentException("Use a fresh absolute evidence directory.", nameof(directory));
        if (!Path.IsPathFullyQualified(renderer) ||
            !string.Equals(Path.GetFileName(renderer), DefaultRendererProviders.VideoExecutableName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Pass the formal Video Renderer executable.", nameof(renderer));
        MpvClient.ValidateLibrary(library, VideoProductCandidateAssets.LibrarySha256);
        string rendererDirectory = Path.GetDirectoryName(Path.GetFullPath(renderer))!;
        DisplayTopology topology = new(1, [new(new DisplayId("hidden-test"), "hidden-container", new(0, 0, 320, 180), 1, 60, true)]);
        HostCrashRecoveryRequest request = new(directory, sourceVideo, Crashes: 4, ObservationDuration: TimeSpan.FromSeconds(1));
        HostCrashRecoveryResult? result = null;
        string? failure = null;
        // Inherited by the Renderer, which reads and clears it; production resolves native/libmpv-2.dll beside it.
        Environment.SetEnvironmentVariable(LibraryVariable, library);
        using HiddenHostDesktop desktop = new();
        try
        {
            using CancellationTokenSource budget = new(TimeSpan.FromSeconds(150));
            Task<HostCrashRecoveryResult> run = Task.Run(() => HostCrashRecoveryDiagnostics.RunAsync(request,
                (configuration, repository) => HostContentRestoreDiagnostics.CreateIsolatedRuntime(configuration, repository,
                    HostContentRestoreDiagnostics.RequireVideoProviders(rendererDirectory), desktop, topology), budget.Token));
            while (!run.IsCompleted) { desktop.Pump(); Thread.Sleep(2); }
            result = run.GetAwaiter().GetResult();
        }
        catch (Exception exception) { failure = exception.GetType().Name + ": " + exception.Message; }
        finally { Environment.SetEnvironmentVariable(LibraryVariable, null); }
        desktop.Dispose();
        failure ??= result?.Failure;
        // Initial Apply plus three restarts, each with its own Replace; the circuit leaves nothing running.
        bool valid = failure is null && result?.Valid == true && result.Crashes.Count == 4 &&
            result.FinalRendererExitCodes.Count == 0 && desktop.Replacements == 4 && desktop.LiveCount == 0;
        Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, "result.json");
        AtomicFileWriter.WriteJsonAsync(output, new { schemaVersion = 1, scope = "C3RendererCrashRecoveryHidden",
            recordedAt = DateTimeOffset.UtcNow, valid, failure, import = result?.Import, initial = result?.Initial,
            crashes = result?.Crashes, journal = result?.Journal, retirement = result?.Retirement,
            finalRendererExitCodes = result?.FinalRendererExitCodes, persistedAssignments = result?.PersistedAssignments,
            replacementCount = desktop.Replacements, hostWindowsCleaned = desktop.LiveCount == 0, shellTouched = false,
            desktopPresentationValidated = false, productionAttachmentEnabled = false }).GetAwaiter().GetResult();
        Console.WriteLine($"C3_CRASH_RECOVERY_PREFLIGHT: valid={valid}; result={output}");
        return valid ? 0 : 1;
    }
}
