using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveWall.Diagnostics;
using LiveWall.Host.Orchestration;
using LiveWall.Platform.Windows.Desktop;
using LiveWall.Platform.Windows.Displays;

namespace LiveWall.DesktopHostDiagnostics;

internal static class VideoProductCandidateCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static async Task<int> RunAsync(VideoCandidateOptions options)
    {
        if (File.Exists(options.ResultJson)) throw new IOException("Use a fresh video result path.");
        Guid runId = Guid.NewGuid();
        DateTimeOffset started = DateTimeOffset.UtcNow;
        // Validate all content/native assets before touching the desktop adapter.
        VideoProductCandidateAssets assets = VideoProductCandidateAssets.Load(options.ContentRoot, options.LibraryPath);
        string? nativeEvidence = options.ControlRecovery ? options.ResultJson + ".native.jsonl" : null;
        if (nativeEvidence is not null && File.Exists(nativeEvidence)) throw new IOException("Use a fresh native control evidence file.");
        if (options.ControlRecovery && !options.PreflightOnly) Directory.CreateDirectory(Path.GetDirectoryName(options.ResultJson)!);
        if (!File.Exists(options.RendererExecutable)) throw new FileNotFoundException("Build the formal Video Renderer first.");
        using WindowsDisplayTopologySource displays = new();
        var topology = displays.Current;
        ShellTopologySnapshot before = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        if (options.PreflightOnly)
        {
            bool ready = before.Availability == ShellTopologyAvailability.Available &&
                before.ExecutionContext.WindowStationName == "WinSta0" && before.ExecutionContext.DesktopName == "Default" &&
                topology.Displays.Count > 0 && !before.ShellDescendants.Any(window => IsOwnedClass(window.ClassName));
            var preflight = new { schemaVersion = 1, scope = "C1VideoCandidateReadOnlyPreflight", runId, startedAt = started,
                fullBuild = before.BuildIdentity.FullBuild, context = before.ExecutionContext, ready,
                classification = ready ? "ReadOnlyPreflightCompleted" : "EnvironmentBlockedOrShellUnavailable",
                librarySha256 = VideoProductCandidateAssets.LibrarySha256, mediaSha256 = VideoProductCandidateAssets.MediaSha256,
                manifestSha256 = assets.ManifestSha256, selectedDisplay = "primary", controlRecovery = options.ControlRecovery,
                displays = topology.Displays.Select(display => new { id = display.Id.Value, display.Bounds, display.ScaleFactor, display.IsPrimary }),
                shell = Summary(before), shellRequestSent = false, surfaceCreated = false, rendererStarted = false,
                attachmentValidationDeferred = true, desktopPresentationValidated = false, productionAttachmentEnabled = false };
            await WriteAsync(options.ResultJson, preflight).ConfigureAwait(false);
            Console.WriteLine($"VIDEO_PREFLIGHT: ready={ready}; result={options.ResultJson}");
            return ready ? 0 : 3;
        }
        ShellTopologySnapshot? requested = null, after = null;
        RendererProductCandidateResult? result = null;
        Exception? failure = null;
        bool attempted = false;
        List<Process> processes = [];
        ShellWindowFingerprint? originalChild = null;
        bool freshChildVerified = false;
        using CancellationTokenSource cancel = new();
        ConsoleCancelEventHandler handler = (_, args) => { args.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            if (before.Availability != ShellTopologyAvailability.Available ||
                before.ExecutionContext.WindowStationName != "WinSta0" || before.ExecutionContext.DesktopName != "Default")
                throw new VideoEnvironmentBlockedException("ShellUnavailableOrWrongInteractiveDesktop");
            if (topology.Displays.Count == 0) throw new VideoEnvironmentBlockedException("NoConnectedDisplay");
            if (before.ShellDescendants.Any(window => IsOwnedClass(window.ClassName)))
                throw new VideoEnvironmentBlockedException("ExistingLiveWallSurface");
            var selection = RendererProductCandidateDiagnostics.ResolveDisplayIds(topology, ProductCandidateDisplaySelection.Primary);
            var target = topology.Displays.Single(display => display.Id == selection[0]);
            Console.WriteLine($"VIDEO_CANDIDATE_READY: display={target.Id}; bounds={target.Bounds}; scale={target.ScaleFactor}; seconds={options.DurationSeconds}.");
            attempted = true;
            result = await RendererProductCandidateDiagnostics.RunAsync(new RendererProductCandidateRequest(
                options.RendererExecutable, new(before.BuildIdentity.Version, before.BuildIdentity.Build, true, before.BuildIdentity.UpdateBuildRevision),
                topology, TimeSpan.FromSeconds(options.DurationSeconds), DisplaySelection: ProductCandidateDisplaySelection.Primary,
                VideoAssets: assets, AttachmentReady: () => requested = WindowsShellTopologyDiagnostics.CaptureSnapshot(),
                FirstFrameCommitted: sessions =>
                {
                    foreach (var session in sessions)
                    {
                        if (session.RendererProcessId is not { } pid) throw new InvalidOperationException("Formal Video PID was not observable.");
                        Process process = Process.GetProcessById(pid);
                        _ = process.SafeHandle; // Hold the actual process identity; do not later reopen a reused PID.
                        processes.Add(process);
                    }
                    Console.WriteLine(options.ControlRecovery
                        ? "VIDEO_FIRST_FRAME_COMMITTED: four 5-second stages: Playing, Paused, ReattachedPaused, Resumed."
                        : $"VIDEO_FIRST_FRAME_COMMITTED: observe moving cyan stripe and full coverage for {options.DurationSeconds} seconds.");
                }, VideoControlRecovery: options.ControlRecovery, VideoControlEvidencePath: nativeEvidence,
                ControlStageChanged: stage =>
                {
                    ShellTopologySnapshot sample = WindowsShellTopologyDiagnostics.CaptureSnapshot();
                    if (sample.Availability != ShellTopologyAvailability.Available || sample.ShellWindowHandle != before.ShellWindowHandle)
                        throw new VideoEnvironmentBlockedException("ShellChangedDuringControlAcceptance");
                    ShellWindowFingerprint child = sample.ShellDescendants.Single(window => window.ClassName.StartsWith("LiveWall.Renderer.Video.", StringComparison.Ordinal));
                    if (stage == VideoControlCandidateStage.Playing) originalChild = child;
                    if (stage == VideoControlCandidateStage.ReattachedPaused)
                    {
                        freshChildVerified = originalChild is not null && child.ProcessId == originalChild.ProcessId &&
                            child.WindowHandle != originalChild.WindowHandle && child.ParentWindowHandle != originalChild.ParentWindowHandle &&
                            sample.ShellDescendants.All(window => window.WindowHandle != originalChild.WindowHandle);
                        if (!freshChildVerified) throw new InvalidOperationException("Video child binding was not replaced in the same Renderer.");
                    }
                    Console.WriteLine($"VIDEO_CONTROL_STAGE: {stage}; observe for 5 seconds.");
                }), cancel.Token).ConfigureAwait(false);
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            Console.CancelKeyPress -= handler;
            try { after = WindowsShellTopologyDiagnostics.CaptureSnapshot(); }
            catch (Exception exception) { failure ??= exception; }
        }
        using WindowsDisplayTopologySource finalDisplays = new();
        ShellMutationAssessment mutation = ShellMutationAssessment.Evaluate(before, requested, after, attempted);
        bool windowsRemain = after is null || after.ShellDescendants.Any(window => IsOwnedClass(window.ClassName));
        List<int?> exitCodes = [];
        foreach (Process process in processes)
        {
            try
            {
                using CancellationTokenSource exitBudget = new(TimeSpan.FromSeconds(3));
                await process.WaitForExitAsync(exitBudget.Token).ConfigureAwait(false);
                exitCodes.Add(process.ExitCode);
            }
            catch (Exception exception) { exitCodes.Add(null); failure ??= exception; }
            finally { process.Dispose(); }
        }
        VideoCandidateCleanupAssessment cleanup = VideoCandidateCleanupAssessment.Evaluate(
            result?.OwnedResourcesCleaned == true, result?.RetirementResult.Graceful == true,
            windowsRemain, result?.Sessions.Count ?? 0, exitCodes);
        object? nativeControl = null;
        if (options.ControlRecovery && result is not null)
        {
            try
            {
                nativeControl = ValidateNativeControls(nativeEvidence!);
                if (!freshChildVerified || result.VideoControl is null)
                    throw new InvalidOperationException("Missing control/binding acceptance evidence.");
            }
            catch (Exception exception) { failure ??= exception; }
        }
        ProductCandidateStructuredResult product = result is not null && after is not null
            ? ProductCandidateStructuredResultWriter.CreateSuccess(runId, started, topology, finalDisplays.Current,
                before, after, result, windowsRemain, mutation.BeforeCount, mutation.AfterCount, mutation.AddedCount)
            : ProductCandidateStructuredResultWriter.CreateFailure(runId, started, topology, before,
                failure ?? new InvalidOperationException("Video candidate failed."), before, after);
        int exitCode = failure is OperationCanceledException ? 130 : failure is VideoEnvironmentBlockedException ? 3 :
            failure is DesktopAttachPointUnavailableException ? 4 : failure is not null || !cleanup.GracefulRetirement ? 8 :
            product.DisplayTopologyChanged || product.ShellGenerationChanged ? 9 :
            mutation.Recovery is ShellMutationRecovery.CleanupIncomplete or ShellMutationRecovery.Inconclusive ? 6 : 0;
        var report = new { schemaVersion = 1, scope = "C1FormalVideoProductCandidate", runId, startedAt = started,
            fullBuild = before.BuildIdentity.FullBuild, context = before.ExecutionContext,
            selectedDisplay = "primary", visibleSeconds = options.DurationSeconds,
            controlRecovery = result?.VideoControl, nativeControl, freshChildVerified = options.ControlRecovery ? freshChildVerified : (bool?)null,
            classification = failure is VideoEnvironmentBlockedException blocked ? "EnvironmentBlocked/" + blocked.Reason :
                exitCode == 0 ? "CandidateRunCompletedAwaitingManual" : exitCode == 6 ? "CandidateRunCompletedWithShellRecoveryIssue" :
                exitCode == 130 ? "Cancelled" : "CandidateFailed",
            exitCode, librarySha256 = VideoProductCandidateAssets.LibrarySha256, mediaSha256 = VideoProductCandidateAssets.MediaSha256,
            manifestSha256 = assets.ManifestSha256, before = Summary(before), afterRequest = requested is null ? null : Summary(requested),
            after = after is null ? null : Summary(after), product,
            ownedResourcesCleanup = cleanup.OwnedResourcesCleaned ? "Complete" : result is null ? "NotConfirmed" : "Incomplete",
            gracefulRetirement = cleanup.GracefulRetirement,
            rendererExitCodes = exitCodes, shellMutationRecovery = mutation,
            failureType = failure?.GetType().Name, manualPresentation = "NotYetConfirmed",
            productionAttachmentEnabled = false };
        string output = Path.GetFullPath(options.ResultJson);
        await WriteAsync(output, report).ConfigureAwait(false);
        Console.WriteLine($"VIDEO_RESULT: exit={exitCode}; ownedCleanup={report.ownedResourcesCleanup}; shellRecovery={mutation.Recovery}; result={output}");
        return exitCode;
    }

    internal static bool IsOwnedClass(string name) => name.StartsWith("LiveWall.DesktopSurface.", StringComparison.Ordinal) ||
        name.StartsWith("LiveWall.Renderer.Video.", StringComparison.Ordinal) ||
        name.StartsWith(RaisedDesktopHostSurfaceLease.WindowClassPrefix, StringComparison.Ordinal);
    private static object ValidateNativeControls(string path)
    {
        JsonElement[] entries = File.ReadLines(path).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        var pause = entries.First(entry => entry.GetProperty("phase").GetString() == "Paused");
        var resume = entries.Last(entry => entry.GetProperty("phase").GetString() == "Playing");
        double from = pause.GetProperty("elapsedMilliseconds").GetDouble() + 200;
        double to = resume.GetProperty("elapsedMilliseconds").GetDouble();
        var health = entries.Where(entry => entry.GetProperty("phase").GetString() == "Health").ToArray();
        var paused = health.Where(entry => entry.GetProperty("elapsedMilliseconds").GetDouble() > from &&
            entry.GetProperty("elapsedMilliseconds").GetDouble() < to).ToArray();
        double Position(JsonElement entry) => entry.GetProperty("snapshot").GetProperty("position").GetDouble();
        if (paused.Length < 4 || paused.Any(entry => !entry.GetProperty("snapshot").GetProperty("paused").GetBoolean()))
            throw new InvalidOperationException("Missing native pause samples.");
        double pausedDelta = paused.Max(Position) - paused.Min(Position);
        var moving = health.Where(entry => entry.GetProperty("elapsedMilliseconds").GetDouble() > to).ToArray();
        if (pausedDelta > 0.09 || moving.Length < 2 || moving.Select(Position).Distinct().Count() < 2 ||
            moving.Any(entry => entry.GetProperty("snapshot").GetProperty("paused").GetBoolean()))
            throw new InvalidOperationException("Native media pause/resume clock acceptance failed.");
        var reattached = entries.Last(entry => entry.GetProperty("phase").GetString() == "Submitted").GetProperty("snapshot");
        if (!reattached.GetProperty("paused").GetBoolean() || !reattached.GetProperty("muted").GetBoolean() ||
            reattached.GetProperty("panScan").GetDouble() != 1)
            throw new InvalidOperationException("Reattachment did not retain paused/muted cover settings.");
        return new { pausedDeltaSeconds = pausedDelta, pausedSampleCount = paused.Length,
            resumedSampleCount = moving.Length, mediaTimeResumed = true, pausedFrameResubmitted = true, settingsPreserved = true };
    }
    internal static object Summary(ShellTopologySnapshot snapshot)
    {
        var structure = snapshot.ShellDescendants.Select(window => new {
            className = window.ClassName.StartsWith("LiveWall.DesktopSurface.", StringComparison.Ordinal) ? "LiveWall.DesktopSurface" :
                window.ClassName.StartsWith("LiveWall.Renderer.Video.", StringComparison.Ordinal) ? "LiveWall.Renderer.Video" :
                window.ClassName.StartsWith(RaisedDesktopHostSurfaceLease.WindowClassPrefix, StringComparison.Ordinal)
                    ? RaisedDesktopHostSurfaceLease.WindowClassPrefix : window.ClassName,
            window.Depth, window.SiblingZOrderIndex, window.Bounds, window.Style, window.ExtendedStyle, ownerIsZero = window.OwnerWindowHandle == 0 });
        string fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(structure, Json)));
        return new { snapshot.Availability, structuralFingerprint = fingerprint, structure };
    }

    private static async Task WriteAsync<T>(string output, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await using FileStream stream = new(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, value, Json, CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class VideoEnvironmentBlockedException(string reason) : InvalidOperationException(reason)
    { public string Reason { get; } = reason; }
}
