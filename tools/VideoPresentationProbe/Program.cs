using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using LiveWall.Contracts.RendererProtocol;
using LiveWall.Infrastructure.FileSystem;
using LiveWall.Infrastructure.Ipc;
using LiveWall.Platform.Windows.Desktop;
using LiveWall.Platform.Windows.Displays;
using LiveWall.Platform.Windows.Lifecycle;
using LiveWall.Renderer.Video.Mpv;

namespace LiveWall.VideoPresentationProbe;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const string TokenVariable = "LIVEWALL_VIDEO_PROBE_TOKEN";

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args is ["--child", string pipe]) return RunChild(pipe);
            if (args.Length is 3 or 4 && args[0] == "--inspect-library")
            {
                string dll = args[1], sha = args[2];
                if (args.Length == 4 && File.Exists(args[3])) throw new IOException("Use a fresh inspection result path.");
                using MpvClient mpv = new(dll, sha);
                mpv.Initialize(0, headless: true); // No HWND, GPU, audio or media is created.
                var inspection = new
                {
                    scope = "LibraryInspectionWithoutPresentation",
                    api = mpv.ClientApiVersion,
                    version = mpv.ReadProperty("mpv-version"),
                    ffmpeg = mpv.ReadProperty("ffmpeg-version"),
                    configuration = mpv.ReadProperty("mpv-configuration"),
                    fitPolicy = new { requested = "cover", keepAspect = mpv.ReadProperty("keepaspect"), panScan = mpv.ReadProperty("panscan") },
                    librarySha256 = sha,
                    presentationValidated = false
                };
                if (args.Length == 4) AtomicFileWriter.WriteJsonAsync(Path.GetFullPath(args[3]), inspection).GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(inspection, Json));
                return 0;
            }
            if (args is ["--generate-fixture", string directory]) { GenerateFixture(directory); return 0; }
            if (args is ["--decode-fixture", string decodeDll, string decodeHash, string fixture, string resultFile])
                return DecodeFixture(decodeDll, decodeHash, fixture, resultFile);
            if (args is ["--render-api-preflight", string renderDll, string renderHash, string renderFixture, string renderResult])
                return RenderApiPreflight.Run(renderDll, renderHash, ValidateFixture(renderFixture), renderResult);
            if (args is ["--protocol-preflight", string renderer, string protocolDll, string protocolFixture, string protocolResult, string scenario])
                return VideoProtocolPreflight.Run(renderer, protocolDll, protocolFixture, ValidateFixture(protocolFixture), protocolResult, scenario);
            if (args is ["--product-chain-preflight", string productRenderer, string productDll, string productFixture, string productResult])
                return VideoCandidatePreflight.Run(productRenderer, productDll, productFixture, productResult);
            if (args is ["--host-control-preflight", string controlRenderer, string controlDll, string controlFixture, string controlOutput, string controlScenario])
                return VideoHostControlPreflight.Run(controlRenderer, controlDll, controlFixture, controlOutput, controlScenario);
            // Deliberately separate from product Program, Apply/Replace and protocol v1 sessions.
            if (args is ["--desktop", string library, string hash, string content, string result,
                "--duration-seconds", string duration, "--confirm-desktop-presentation"] &&
                int.TryParse(duration, CultureInfo.InvariantCulture, out int seconds) && seconds is >= 8 and <= 30)
                return RunDesktopAsync(library, hash, content, result, seconds).GetAwaiter().GetResult();
            Console.Error.WriteLine("Use --inspect-library <absolute-dll> <sha256>, --generate-fixture <fresh-root>, or --desktop <dll> <sha256> <fixture-root> <fresh-result-json> --duration-seconds 8..30 --confirm-desktop-presentation. Desktop mode is a visible, explicit C0 diagnostic, never product first-frame evidence.");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static int RunChild(string pipeName)
    {
        string token = Environment.GetEnvironmentVariable(TokenVariable) ?? throw new InvalidOperationException("Missing one-time credential.");
        Environment.SetEnvironmentVariable(TokenVariable, null);
        using WindowsProcessShutdownOrder order = WindowsProcessShutdownOrder.ForRenderer();
        using var pipe = LocalNamedPipeFactory.CreateClient(pipeName);
        pipe.Connect(10_000);
        var channel = new LengthPrefixedJsonChannel(pipe);
        try
        {
            channel.WriteAsync(new ProbeReply("Hello", token)).AsTask().GetAwaiter().GetResult();
            ProbeCommand attach = channel.ReadAsync<ProbeCommand>().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            if (attach.Type != "Attach" || attach.Surface is null || attach.Library is null || attach.Hash is null || attach.Media is null)
                throw new InvalidDataException("C0 requires exactly one initial Attach.");
            using ProbeWindow child = new(attach.Surface.WindowHandle, attach.Surface.Width, attach.Surface.Height);
            MpvClient mpv = new(attach.Library, attach.Hash);
            try
            {
                mpv.Initialize(unchecked((ulong)child.Handle.ToInt64()));
                mpv.LoadLocalVideo(attach.Media);
                channel.WriteAsync(new ProbeReply("Attached", ChildParentValidated: true, Geometry: child.ReadGeometry())).AsTask().GetAwaiter().GetResult();
                Stopwatch life = Stopwatch.StartNew();
                Task<ProbeCommand> next = channel.ReadAsync<ProbeCommand>().AsTask();
                while (life.Elapsed < TimeSpan.FromSeconds(90))
                {
                    child.Pump();
                    MpvOutputEvidence evidence = mpv.Poll();
                    if (!next.IsCompleted) { Thread.Sleep(10); continue; }
                    ProbeCommand request = next.GetAwaiter().GetResult();
                    switch (request.Type)
                    {
                        case "Snapshot": break;
                        case "Pause": mpv.Pause(true); break;
                        case "Resume": mpv.Pause(false); break;
                        case "Resize" when request.Surface is not null:
                            child.Resize(request.Surface.Width, request.Surface.Height); break;
                        case "Shutdown":
                            ReleaseMpvWhilePumping(mpv, child); child.Dispose();
                            channel.WriteAsync(new ProbeReply("ShutdownCompleted", OwnedWindowDestroyed: true)).AsTask().GetAwaiter().GetResult();
                            return 0;
                        default: throw new InvalidDataException("Unsupported C0 diagnostic command.");
                    }
                    channel.WriteAsync(new ProbeReply(request.Type, Evidence: evidence,
                        Paused: mpv.ReadProperty("pause") == "yes", Geometry: child.ReadGeometry())).AsTask().GetAwaiter().GetResult();
                    next = channel.ReadAsync<ProbeCommand>().AsTask();
                }
                throw new TimeoutException("C0 child lifetime limit reached.");
            }
            finally { ReleaseMpvWhilePumping(mpv, child); }
        }
        finally { channel.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void ReleaseMpvWhilePumping(MpvClient mpv, ProbeWindow child)
    {
        // The VO may synchronously interact with the embedding HWND while terminating.
        // Keep its owning STA responsive; the parent enforces an independent kill budget.
        Task release = Task.Run(mpv.Dispose);
        while (!release.IsCompleted) { child.Pump(); Thread.Sleep(5); }
        release.GetAwaiter().GetResult();
    }

    private static async Task<int> RunDesktopAsync(string dll, string hash, string contentRoot, string resultPath, int seconds)
    {
        MpvClient.ValidateLibrary(dll, hash);
        string media = ValidateFixture(contentRoot);
        string output = Path.GetFullPath(resultPath);
        if (File.Exists(output)) throw new IOException("Use a fresh result path; old evidence is never overwritten.");
        using WindowsProcessShutdownOrder shutdownOrder = WindowsProcessShutdownOrder.ForHost();
        ShellTopologySnapshot before = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        if (before.Availability != ShellTopologyAvailability.Available ||
            before.ExecutionContext.WindowStationName != "WinSta0" || before.ExecutionContext.DesktopName != "Default")
            throw new InvalidOperationException("EnvironmentBlocked: interactive WinSta0\\Default required.");
        using WindowsDisplayTopologySource topology = new();
        if (topology.Current.Displays.Count != 1) throw new InvalidOperationException("C0 currently requires one physical display; do not disconnect devices during the probe.");
        var initialDisplays = topology.Current.Displays.ToArray();
        var bounds = topology.Current.Displays[0].Bounds;
        ShellWindowBounds region = new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        string pipeName = @"LOCAL\livewall.video-c0." + Guid.NewGuid().ToString("N");
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var pipe = LocalNamedPipeFactory.CreateServer(pipeName, 1, true);
        await using LengthPrefixedJsonChannel channel = new(pipe);
        using CancellationTokenSource total = new(TimeSpan.FromSeconds(seconds + 35));
        using CancellationTokenSource cancel = new();
        ConsoleCancelEventHandler handler = (_, evt) => { evt.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        using CancellationTokenSource run = CancellationTokenSource.CreateLinkedTokenSource(total.Token, cancel.Token);
        Process? renderer = null;
        RaisedDesktopHostSurfaceLease? host = null;
        bool shutdownCompleted = false, childDestroyed = false, hostDestroyed = false, forced = false;
        bool parentValidated = false, paused = false, pauseStable = false, resumeAdvanced = false;
        bool resizeExercised = false;
        ProbeWindowGeometry? initialGeometry = null, finalGeometry = null;
        string? failure = null;
        List<MpvOutputEvidence> samples = [];
        ShellTopologySnapshot? attached = null;
        try
        {
            // This is the existing explicitly visible diagnostic lease, NOT a product provisional
            // Surface. No SessionCoordinator, Replace, or FirstFramePresented event is involved.
            host = await RaisedDesktopDiagnostics.AcquireHostSurfaceAsync(region, null, run.Token).ConfigureAwait(false);
            attached = WindowsShellTopologyDiagnostics.CaptureSnapshot();
            ProcessStartInfo start = new(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--child"); start.ArgumentList.Add(pipeName);
            start.Environment[TokenVariable] = token;
            renderer = Process.Start(start) ?? throw new InvalidOperationException("Unable to start owned C0 Renderer.");
            await pipe.WaitForConnectionAsync(run.Token).ConfigureAwait(false);
            ProbeReply hello = await channel.ReadAsync<ProbeReply>(run.Token).ConfigureAwait(false);
            if (hello.Type != "Hello" || hello.Token != token) throw new InvalidDataException("C0 authentication failed.");
            await channel.WriteAsync(new ProbeCommand("Attach", new AttachSurfacePayload(host.WindowHandle, host.Width, host.Height, 1),
                dll, hash, media), run.Token).ConfigureAwait(false);
            ProbeReply ready = await channel.ReadAsync<ProbeReply>(run.Token).ConfigureAwait(false);
            parentValidated = ready.Type == "Attached" && ready.ChildParentValidated;
            initialGeometry = ready.Geometry;
            if (!parentValidated) throw new InvalidDataException("Renderer child parent was not validated.");
            Console.WriteLine("C0_PRESENTATION_ATTEMPT: observe moving cyan stripe over changing orange/yellow; icons/taskbar must stay above. Brief pause and child resize follow. This is not product FirstFramePresented.");
            Stopwatch outputWait = Stopwatch.StartNew();
            while (true)
            {
                ProbeReply snapshot = await ExchangeAsync(channel, new("Snapshot"), run.Token).ConfigureAwait(false);
                if (snapshot.Evidence is { } evidence) samples.Add(evidence);
                if (samples.LastOrDefault()?.OutputAttempted == true) break;
                if (outputWait.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("No libmpv output-backend readiness within 10 seconds.");
                await Task.Delay(100, run.Token).ConfigureAwait(false);
            }
            await Task.Delay(TimeSpan.FromSeconds(seconds / 2d), run.Token).ConfigureAwait(false);
            ProbeReply pause = await ExchangeAsync(channel, new("Pause"), run.Token).ConfigureAwait(false);
            await Task.Delay(400, run.Token).ConfigureAwait(false);
            ProbeReply firstPause = await ExchangeAsync(channel, new("Snapshot"), run.Token).ConfigureAwait(false);
            await Task.Delay(800, run.Token).ConfigureAwait(false);
            ProbeReply secondPause = await ExchangeAsync(channel, new("Snapshot"), run.Token).ConfigureAwait(false);
            paused = pause.Paused && firstPause.Paused && secondPause.Paused;
            pauseStable = firstPause.Evidence?.PositionSeconds is double first &&
                secondPause.Evidence?.PositionSeconds is double second && Math.Abs(first - second) < 0.08;
            await ExchangeAsync(channel, new("Resume"), run.Token).ConfigureAwait(false);
            await Task.Delay(650, run.Token).ConfigureAwait(false);
            ProbeReply resumed = await ExchangeAsync(channel, new("Snapshot"), run.Token).ConfigureAwait(false);
            if (resumed.Evidence is { } resumedEvidence) samples.Add(resumedEvidence);
            resumeAdvanced = !resumed.Paused && secondPause.Evidence?.PositionSeconds is double oldPosition &&
                resumed.Evidence?.PositionSeconds is double newPosition && Math.Abs(newPosition - oldPosition) > 0.1;
            AttachSurfacePayload smaller = new(host.WindowHandle, Math.Max(1, host.Width - 64), Math.Max(1, host.Height - 64), 1);
            await ExchangeAsync(channel, new("Resize", smaller), run.Token).ConfigureAwait(false);
            await Task.Delay(600, run.Token).ConfigureAwait(false);
            await ExchangeAsync(channel, new("Resize", new(host.WindowHandle, host.Width, host.Height, 1)), run.Token).ConfigureAwait(false);
            resizeExercised = true;
            await Task.Delay(TimeSpan.FromSeconds(seconds / 2d), run.Token).ConfigureAwait(false);
            ProbeReply final = await ExchangeAsync(channel, new("Snapshot"), run.Token).ConfigureAwait(false);
            if (final.Evidence is { } last) samples.Add(last);
            finalGeometry = final.Geometry;
        }
        catch (Exception exception) { failure = exception.GetType().Name + ": " + exception.Message; }
        finally
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
            if (renderer is not null)
            {
                try
                {
                    if (!renderer.HasExited && pipe.IsConnected)
                    {
                        ProbeReply stopped = await ExchangeAsync(channel, new("Shutdown"), cleanup.Token).ConfigureAwait(false);
                        shutdownCompleted = stopped.Type == "ShutdownCompleted";
                        childDestroyed = stopped.OwnedWindowDestroyed;
                    }
                    await renderer.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failure ??= "Cleanup: " + exception.GetType().Name;
                    if (!renderer.HasExited)
                    {
                        forced = true;
                        try
                        {
                            renderer.Kill(entireProcessTree: true);
                            using CancellationTokenSource emergency = new(TimeSpan.FromSeconds(2));
                            await renderer.WaitForExitAsync(emergency.Token).ConfigureAwait(false);
                        }
                        catch (Exception forceError) { failure += "; Forced cleanup: " + forceError.GetType().Name; }
                    }
                }
            }
            if (host is not null)
            {
                try { await host.DisposeAsync().ConfigureAwait(false); hostDestroyed = true; }
                catch (Exception exception) { failure ??= "Host cleanup: " + exception.GetType().Name; }
            }
            Console.CancelKeyPress -= handler;
        }
        ShellTopologySnapshot after = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        bool generationSame = SameShellGeneration(before, after);
        bool topologySame = topology.Refresh().Displays.SequenceEqual(initialDisplays);
        int beforeWorkers = WorkerCount(before), afterWorkers = WorkerCount(after);
        bool ownedWindowsRemain = after.ShellDescendants.Any(window =>
            window.ClassName.StartsWith("LiveWall.VideoProbe.", StringComparison.Ordinal) ||
            window.ClassName.StartsWith(RaisedDesktopHostSurfaceLease.WindowClassPrefix, StringComparison.Ordinal));
        int? exitCode = renderer?.HasExited == true ? renderer.ExitCode : null;
        bool rendererDistinct = renderer is not null && renderer.Id != Environment.ProcessId;
        renderer?.Dispose();
        bool clean = shutdownCompleted && childDestroyed && hostDestroyed && !ownedWindowsRemain && !forced && exitCode == 0;
        MpvViewportEvidence? finalViewport = samples.LastOrDefault()?.Viewport;
        bool geometryValid = initialGeometry?.ChildFillsHost == true && finalGeometry?.ChildFillsHost == true &&
            finalGeometry.HostWidth == bounds.Width && finalGeometry.HostHeight == bounds.Height &&
            finalViewport?.Width == bounds.Width && finalViewport.Height == bounds.Height &&
            finalViewport.CoverConfigured && finalViewport.HasNoLetterbox;
        bool backendChecks = parentValidated && paused && pauseStable && resumeAdvanced && resizeExercised && samples.Any(s => s.OutputAttempted) &&
            samples.LastOrDefault()?.LoopRestarts > 1 && geometryValid;
        if (!geometryValid) failure ??= "C0GeometryValidationFailed: missing or inconsistent client/VO dimensions, cover options, or video margins.";
        string mutation = ClassifyMutation(before, attached, after);
        bool environmentStable = generationSame && topologySame;
        var result = new
        {
            schemaVersion = 1,
            scope = "C0DiagnosticPresentationOnly",
            recordedAt = DateTimeOffset.UtcNow,
            classification = !environmentStable ? "EnvironmentChanged" : failure is null && backendChecks && clean ? "PresentationAttemptCompleted" : "ProbeFailed",
            failure,
            librarySha256 = hash,
            mediaSha256 = HashFile(media),
            before = Summary(before),
            afterRequest = attached is null ? null : Summary(attached),
            after = Summary(after),
            rendererProcessDistinct = rendererDistinct,
            childParentValidated = parentValidated,
            samples,
            playback = new { paused, pauseStable, resumeAdvanced, resizeExercised },
            geometry = new { requestedFit = "cover", initialGeometry, finalGeometry, finalViewport, validated = geometryValid },
            cleanup = new
            {
                shutdownCompleted,
                childDestroyed,
                hostDestroyed,
                ownedWindowsRemain,
                forced,
                rendererExitCode = exitCode,
                ownedResourcesCleanup = clean ? "Complete" : "Incomplete",
                shellMutationRecovery = mutation,
                directShellWorkerCountBefore = beforeWorkers,
                directShellWorkerCountAfter = afterWorkers
            },
            shellGenerationUnchanged = generationSame,
            displayTopologyUnchanged = topologySame,
            manualPresentation = "NotYetConfirmed",
            firstFramePresentedEmitted = false,
            productSessionCreated = false,
            productionAttachmentEnabled = false
        };
        await AtomicFileWriter.WriteJsonAsync(output, result).ConfigureAwait(false);
        Console.WriteLine($"C0_RESULT: {result.classification}; manualPresentation=NotYetConfirmed; result={output}");
        return failure is not null || !backendChecks || !clean ? 1 : !environmentStable ? 3 : mutation is "NotRequired" or "Recovered" ? 0 : 6;
    }

    private static async Task<ProbeReply> ExchangeAsync(LengthPrefixedJsonChannel channel, ProbeCommand request, CancellationToken token)
    {
        await channel.WriteAsync(request, token).ConfigureAwait(false);
        ProbeReply reply = await channel.ReadAsync<ProbeReply>(token).ConfigureAwait(false);
        if (reply.Type != (request.Type == "Shutdown" ? "ShutdownCompleted" : request.Type)) throw new InvalidDataException("C0 reply ordering mismatch.");
        return reply;
    }

    private static int DecodeFixture(string dll, string hash, string root, string resultPath)
    {
        string media = ValidateFixture(root);
        if (File.Exists(resultPath)) throw new IOException("Use a fresh result path.");
        using MpvClient mpv = new(dll, hash);
        mpv.Initialize(0, headless: true);
        mpv.LoadLocalVideo(media);
        Stopwatch timeout = Stopwatch.StartNew();
        MpvOutputEvidence evidence;
        do
        {
            evidence = mpv.Poll();
            if (evidence.FileLoaded && evidence.PlaybackRestarted && evidence.PositionSeconds > 0.4) break;
            Thread.Sleep(10);
        } while (timeout.Elapsed < TimeSpan.FromSeconds(8));
        bool valid = evidence.FileLoaded && evidence.PlaybackRestarted && evidence.PositionSeconds > 0.4;
        var result = new
        {
            schemaVersion = 1,
            scope = "HeadlessDecodeOnly",
            valid,
            recordedAt = DateTimeOffset.UtcNow,
            mpvVersion = mpv.ReadProperty("mpv-version"),
            ffmpegVersion = mpv.ReadProperty("ffmpeg-version"),
            codec = mpv.ReadProperty("video-codec"),
            librarySha256 = hash,
            mediaSha256 = HashFile(media),
            evidence,
            windowCreated = false,
            gpuOutputCreated = false,
            desktopPresentationValidated = false
        };
        AtomicFileWriter.WriteJsonAsync(Path.GetFullPath(resultPath), result).GetAwaiter().GetResult();
        Console.WriteLine(JsonSerializer.Serialize(result, Json));
        return valid ? 0 : 1;
    }

    private static object Summary(ShellTopologySnapshot snapshot) => new
    {
        snapshot.BuildIdentity.FullBuild,
        snapshot.ExecutionContext,
        shellAvailable = snapshot.Availability == ShellTopologyAvailability.Available,
        structure = snapshot.ShellDescendants.Select(w => new { ClassName = StableClassName(w.ClassName), w.Depth, w.SiblingZOrderIndex, w.Bounds, w.Style, w.ExtendedStyle })
    };
    private static string StableClassName(string name) => name.StartsWith(RaisedDesktopHostSurfaceLease.WindowClassPrefix, StringComparison.Ordinal)
        ? RaisedDesktopHostSurfaceLease.WindowClassPrefix : name.StartsWith("LiveWall.VideoProbe.", StringComparison.Ordinal) ? "LiveWall.VideoProbe" : name;
    private static int WorkerCount(ShellTopologySnapshot snapshot) => snapshot.ShellDescendants.Count(w =>
        w.ClassName == "WorkerW" && w.ParentWindowHandle == snapshot.ShellWindowHandle);
    private static bool SameShellGeneration(ShellTopologySnapshot before, ShellTopologySnapshot after) =>
        after.Availability == ShellTopologyAvailability.Available && before.ShellWindowHandle == after.ShellWindowHandle &&
        before.TopLevelWindows.Single(w => w.WindowHandle == before.ShellWindowHandle).ProcessId ==
        after.TopLevelWindows.Single(w => w.WindowHandle == after.ShellWindowHandle).ProcessId;
    private static string ClassifyMutation(ShellTopologySnapshot before, ShellTopologySnapshot? requested, ShellTopologySnapshot after)
    {
        if (!SameShellGeneration(before, after)) return "Inconclusive";
        static Dictionary<ulong, ShellWindowFingerprint> Workers(ShellTopologySnapshot snapshot) => snapshot.ShellDescendants
            .Where(w => w.ClassName == "WorkerW" && w.ParentWindowHandle == snapshot.ShellWindowHandle).ToDictionary(w => w.WindowHandle);
        var baseline = Workers(before);
        var final = Workers(after);
        if (final.Keys.Except(baseline.Keys).Any()) return "CleanupIncomplete";
        if (baseline.Count != final.Count || baseline.Any(pair => !final.TryGetValue(pair.Key, out var other) ||
            pair.Value.ProcessId != other.ProcessId || pair.Value.OwnerWindowHandle != other.OwnerWindowHandle ||
            pair.Value.Bounds != other.Bounds || pair.Value.Style != other.Style || pair.Value.ExtendedStyle != other.ExtendedStyle))
            return "Inconclusive";
        return requested is not null && Workers(requested).Keys.Except(baseline.Keys).Any() ? "Recovered" : "NotRequired";
    }
    private static string HashFile(string path) { using FileStream input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }

    private static string ValidateFixture(string root)
    {
        string fullRoot = Path.GetFullPath(root);
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fullRoot, "manifest.json")));
        if (manifest.RootElement.GetProperty("schemaVersion").GetInt32() != 1 ||
            manifest.RootElement.GetProperty("id").GetString() != "livewall.diagnostic.video-c0" ||
            manifest.RootElement.GetProperty("type").GetString() != "video" ||
            manifest.RootElement.GetProperty("defaults").GetProperty("fit").GetString() != "cover" ||
            manifest.RootElement.GetProperty("entry").GetString() != "content/motion.avi")
            throw new InvalidDataException("C0 accepts only its generated native fixture, not general content ingestion.");
        string media = Path.Combine(fullRoot, "content", "motion.avi");
        using JsonDocument identity = JsonDocument.Parse(File.ReadAllText(Path.Combine(fullRoot, "fixture.json")));
        if (HashFile(media) != identity.RootElement.GetProperty("sha256").GetString()) throw new InvalidDataException("Fixture hash mismatch.");
        return media;
    }

    private static void GenerateFixture(string directory)
    {
        string root = Path.GetFullPath(directory);
        if (Directory.Exists(root)) throw new IOException("Fixture directory must be fresh.");
        Directory.CreateDirectory(Path.Combine(root, "content"));
        string video = Path.Combine(root, "content", "motion.avi");
        SyntheticVideo.Write(video);
        File.WriteAllText(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            id = "livewall.diagnostic.video-c0",
            version = "1.0.0",
            title = "LiveWall original motion fixture",
            type = "video",
            entry = "content/motion.avi",
            permissions = new { network = false, audioCapture = false, pointerInput = false, systemMetrics = false },
            defaults = new { fit = "cover", fpsLimit = 24, muted = true }
        }, Json));
        File.WriteAllText(Path.Combine(root, "fixture.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            generator = "SyntheticVideo/v1",
            width = SyntheticVideo.Width,
            height = SyntheticVideo.Height,
            fps = SyntheticVideo.Fps,
            frames = SyntheticVideo.Frames,
            durationSeconds = 3,
            sha256 = HashFile(video),
            source = "Original deterministic geometric pixels; no external assets",
            dataLicense = "CC0-1.0 (generated fixture data only; not project source or libmpv)"
        }, Json));
        Console.WriteLine($"C0_FIXTURE: root={root}; sha256={HashFile(video)}");
    }

    private sealed record ProbeCommand(string Type, AttachSurfacePayload? Surface = null,
        string? Library = null, string? Hash = null, string? Media = null);
    private sealed record ProbeReply(string Type, string? Token = null, MpvOutputEvidence? Evidence = null,
        bool ChildParentValidated = false, bool Paused = false, bool OwnedWindowDestroyed = false, ProbeWindowGeometry? Geometry = null);
}
