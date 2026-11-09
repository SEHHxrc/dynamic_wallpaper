using LiveWall.Domain.Displays;
using LiveWall.Host.Orchestration;
using LiveWall.Infrastructure.FileSystem;
using LiveWall.Renderer.Video.Mpv;

namespace LiveWall.VideoPresentationProbe;

// C2: the real Host runtime, import pipeline, persistent library, JSON configuration store and default
// Video provider across a Host restart. Only the desktop is a private hidden container, so no Shell is
// touched and nothing is shown.
internal static class VideoHostRestorePreflight
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
        HostContentRestoreRequest request = new(directory, sourceVideo, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        HostContentRestoreResult? result = null;
        string? failure = null;
        // Inherited by the Renderer, which reads and clears it; production resolves native/libmpv-2.dll beside it.
        Environment.SetEnvironmentVariable(LibraryVariable, library);
        using HiddenHostDesktop desktop = new();
        try
        {
            using CancellationTokenSource budget = new(TimeSpan.FromSeconds(120));
            Task<HostContentRestoreResult> run = Task.Run(() => HostContentRestoreDiagnostics.RunAsync(request,
                (configuration, repository) => HostContentRestoreDiagnostics.CreateIsolatedRuntime(configuration, repository,
                    HostContentRestoreDiagnostics.RequireVideoProviders(rendererDirectory), desktop, topology), budget.Token));
            while (!run.IsCompleted) { desktop.Pump(); Thread.Sleep(2); }
            result = run.GetAwaiter().GetResult();
        }
        catch (Exception exception) { failure = exception.GetType().Name + ": " + exception.Message; }
        finally { Environment.SetEnvironmentVariable(LibraryVariable, null); }
        desktop.Dispose();
        failure ??= result?.Failure;
        bool rendererExits = result is not null && result.Phases.All(phase => phase.RendererExitCodes is [0]);
        bool valid = failure is null && result?.Valid == true && rendererExits &&
            desktop.Replacements == 2 && desktop.LiveCount == 0;
        Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, "result.json");
        AtomicFileWriter.WriteJsonAsync(output, new { schemaVersion = 1, scope = "C2HostContentRestoreHidden",
            recordedAt = DateTimeOffset.UtcNow, valid, failure, import = result?.Import, contentRoot = result?.ContentRoot,
            phases = result?.Phases, rendererExitCodesZero = rendererExits, replacementCount = desktop.Replacements,
            hostWindowsCleaned = desktop.LiveCount == 0, shellTouched = false, desktopPresentationValidated = false,
            productionAttachmentEnabled = false }).GetAwaiter().GetResult();
        Console.WriteLine($"C2_HOST_RESTORE_PREFLIGHT: valid={valid}; result={output}");
        return valid ? 0 : 1;
    }
}
