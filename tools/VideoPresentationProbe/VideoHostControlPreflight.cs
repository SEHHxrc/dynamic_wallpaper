using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LiveWall.Application.Importing;
using LiveWall.Application.Layouts;
using LiveWall.Application.Library;
using LiveWall.Application.Policies;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Domain.Playback;
using LiveWall.Domain.Sessions;
using LiveWall.Domain.Wallpapers;
using LiveWall.Host.CommandLoop;
using LiveWall.Host.Orchestration;
using LiveWall.Infrastructure.FileSystem;
using LiveWall.Renderer.Video.Mpv;

namespace LiveWall.VideoPresentationProbe;

// Formal Host + Video executable, private hidden containers, bounded read-only native
// evidence. No Shell, experimental attachment override, or new Renderer DTOs.
internal static class VideoHostControlPreflight
{
    public static int Run(string renderer, string library, string content, string directory, string scenario)
    {
        if (scenario is not ("controls" or "candidate-plan" or "load-failure" or "first-frame-timeout" or "process-exit" or "pipe-disconnect"))
            throw new ArgumentException("Unknown Host control scenario.", nameof(scenario));
        if (!Path.IsPathFullyQualified(directory) || Directory.Exists(directory) || File.Exists(directory))
            throw new ArgumentException("Use a fresh absolute evidence directory.", nameof(directory));
        VideoProductCandidateAssets assets = VideoProductCandidateAssets.Load(content, library);
        Directory.CreateDirectory(directory);
        using HiddenDesktop desktop = new();
        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(65));
        Task<object> run = Task.Run(() => RunAsync(renderer, assets, directory, scenario, desktop, budget.Token));
        while (!run.IsCompleted) { desktop.Pump(); Thread.Sleep(2); }
        object? result = null;
        string? failure = null;
        try { result = run.GetAwaiter().GetResult(); }
        catch (Exception exception) { failure = exception.GetType().Name + ": " + exception.Message; }
        desktop.Dispose();
        bool valid = failure is null && desktop.LiveCount == 0;
        AtomicFileWriter.WriteJsonAsync(Path.Combine(directory, "result.json"), new { schemaVersion = 1,
            scope = "C13FormalVideoHostHiddenControl", recordedAt = DateTimeOffset.UtcNow, scenario, valid, failure,
            result, hostWindowsCleaned = desktop.LiveCount == 0, shellTouched = false,
            desktopPresentationValidated = false, productionAttachmentEnabled = false }).GetAwaiter().GetResult();
        Console.WriteLine($"C13_HOST_CONTROL: valid={valid}; scenario={scenario}; result={Path.Combine(directory, "result.json")}");
        return valid ? 0 : 1;
    }

    private static async Task<object> RunAsync(string executable, VideoProductCandidateAssets assets, string directory,
        string scenario, HiddenDesktop desktop, CancellationToken token)
    {
        using Launcher launcher = new();
        RendererProcessProvider native = new(new(new(new RendererId("video"), "LiveWall Video",
            new HashSet<WallpaperKind> { WallpaperKind.Video }, new HashSet<string> { "Video", "HwndChild", "DirectComposition" }),
            executable, "c13-video", ["Video", "HwndChild", "DirectComposition"], "zh-CN", TimeSpan.FromSeconds(10)), launcher,
            (_, iteration, ordinal) => new Dictionary<string, string> {
                ["LIVEWALL_VIDEO_LIBMPV_PATH"] = assets.LibraryPath,
                [VideoControlEvidence.EnvironmentVariable] = Path.Combine(directory, $"native-{iteration}-{ordinal}.jsonl") });
        WallpaperLibraryEntry good = assets.Entry;
        string badRoot = Path.Combine(directory, "invalid-media");
        Directory.CreateDirectory(Path.Combine(badRoot, "content"));
        await File.WriteAllTextAsync(Path.Combine(badRoot, "content", "broken.avi"), "not a media stream", token);
        WallpaperLibraryEntry bad = good with { Definition = good.Definition with {
            Id = new WallpaperId("diagnostic.invalid-video"), EntryPoint = "content/broken.avi" }, CanonicalContentRoot = badRoot };
        Provider provider = new(native, scenario == "first-frame-timeout");
        SessionCoordinator coordinator = new(new Repository(good, bad), new DefaultLayoutPlanner(),
            new RendererProviderSelector([provider]), desktop, new("win-x64", true, new HashSet<string>()),
            TimeSpan.FromSeconds(3));
        HostCommandLoop host = new(coordinator, new DefaultPlaybackPolicyEvaluator());
        DisplayId display = new("hidden-control");
        await host.SendAsync(new DisplayTopologyChangedCommand(new(1, [new(display, "hidden", new(0, 0, 320, 180), 1, 60, true)])), token);
        object? controls = null;
        object? expectedFailure = null;
        SessionRetirementBatchResult? retirement = null;
        ActiveRendererFailureException[] observedHostFailures = [];
        try
        {
            ApplyGenerationResult initial = await Apply(good.Definition.Id);
            Require(initial.State == ApplyGenerationTerminalState.Succeeded && desktop.Replacements == 1, "Initial product Apply failed.");
            PreparedSession active = host.GetPreparedSessionsForDiagnostics().Single();
            RendererProtocolSession renderer = (RendererProtocolSession)active.Renderer;
            ulong oldHost = active.Surfaces.Single().Surface.WindowHandle;
            ulong oldChild = ProbeWindow.FirstChild(oldHost);
            Require(ProbeWindow.ChildMatches(oldChild, oldHost, renderer.ProcessId, 320, 180), "Initial child identity mismatch.");
            string trace = Path.Combine(directory, "native-1-1.jsonl");
            if (scenario == "candidate-plan")
            {
                var plan = await VideoControlCandidateDiagnostics.RunAsync(host, TimeSpan.FromMilliseconds(250), null, token);
                Require(plan.SameRenderer && plan.FreshSurface && plan.Stages.Count == 4 &&
                    plan.Stages.All(stage => stage.Held >= TimeSpan.FromMilliseconds(250)), "Control acceptance plan failed.");
                controls = plan;
            }
            else if (scenario == "controls")
            {
                await host.SendAsync(new ChangePlaybackIntentCommand(UserPlaybackIntent.Pause), token);
                JsonElement pause = await Evidence(trace, "Paused", 0, token);
                await Task.Delay(800, token);
                JsonElement[] pausedSamples = Read(trace).Where(e => e.GetProperty("phase").GetString() == "Health" &&
                    e.GetProperty("elapsedMilliseconds").GetDouble() > pause.GetProperty("elapsedMilliseconds").GetDouble() + 150).ToArray();
                Require(pausedSamples.Length >= 2, "Missing native paused time samples.");
                double pausedDelta = Position(pausedSamples[^1]) - Position(pausedSamples[0]);
                Require(Math.Abs(pausedDelta) <= 0.09 && pausedSamples.All(e => e.GetProperty("snapshot").GetProperty("paused").GetBoolean()), "Native media clock did not pause.");
                int previous = 0;
                foreach (FitMode fit in new[] { FitMode.Cover, FitMode.Contain, FitMode.Stretch, FitMode.Center, FitMode.Contain })
                {
                    await renderer.SendAsync(new SetRendererFit(1, fit), token);
                    JsonElement applied = await Evidence(trace, "Fit", previous, token);
                    previous = applied.GetProperty("ordinal").GetInt32();
                }
                foreach ((double volume, bool muted) in new[] { (0d, true), (0.37, false), (0.83, true) })
                {
                    await renderer.SendAsync(new SetRendererVolume(1, volume, muted), token);
                    JsonElement applied = await Evidence(trace, "Volume", previous, token);
                    Require(applied.GetProperty("snapshot").GetProperty("volume").GetDouble() == volume * 100 &&
                        applied.GetProperty("snapshot").GetProperty("muted").GetBoolean() == muted, "Native volume/mute mismatch.");
                    previous = applied.GetProperty("ordinal").GetInt32();
                }
                await renderer.SendAsync(new SetRendererBounds(2, new(0, 0, 640, 360), 1), token);
                await Frame(renderer, 1, token);
                Require(ProbeWindow.ChildMatches(oldChild, oldHost, renderer.ProcessId, 640, 360), "Current-binding bounds failed.");
                await host.SendAsync(new ExplorerRestartedCommand(), token); // Synthetic Host recovery; never restarts Explorer.
                long recoveryId = host.GetRecoveryOperationIdsForDiagnostics().Single();
                DesktopRecoveryResult recovery = await host.WaitForDesktopRecoveryAsync(recoveryId, token);
                PreparedSession recovered = host.GetPreparedSessionsForDiagnostics().Single();
                ulong parent = recovered.Surfaces.Single().Surface.WindowHandle;
                ulong child = ProbeWindow.FirstChild(parent);
                Require(recovery.TerminalState == DesktopRecoveryTerminalState.Succeeded && recovery.CompletionMode == DesktopRecoveryCompletionMode.Reattached &&
                    ReferenceEquals(recovered.Renderer, renderer) && parent != oldHost && child != oldChild && !ProbeWindow.WindowExists(oldChild) &&
                    ProbeWindow.ChildMatches(child, parent, renderer.ProcessId, 320, 180), "Paused Host recovery did not create a fresh binding in the same Renderer.");
                JsonElement submitted = Read(trace).Last(e => e.GetProperty("phase").GetString() == "Submitted");
                JsonElement snapshot = submitted.GetProperty("snapshot");
                Require(snapshot.GetProperty("paused").GetBoolean() && snapshot.GetProperty("panScan").GetDouble() == 0 &&
                    snapshot.GetProperty("volume").GetDouble() == 83 && snapshot.GetProperty("muted").GetBoolean(), "Controls were lost during reattachment.");
                previous = Read(trace).Max(e => e.GetProperty("ordinal").GetInt32());
                await host.SendAsync(new ChangePlaybackIntentCommand(UserPlaybackIntent.Play), token);
                JsonElement resumed = await Evidence(trace, "Playing", previous, token);
                await Task.Delay(700, token);
                JsonElement later = Read(trace).Last(e => e.GetProperty("phase").GetString() == "Health");
                double advanced = (Position(later) - Position(resumed) + 3) % 3;
                Require(!later.GetProperty("snapshot").GetProperty("paused").GetBoolean() && advanced > 0.15, "Native media clock did not resume.");
                controls = new { pausedDeltaSeconds = pausedDelta, resumedAdvanceSeconds = advanced,
                    sameRenderer = true, freshBinding = true, settingsPreserved = true, boundsAcknowledgedGeneration = 1,
                    recovery, playback = renderer.GetPlaybackEvidenceForDiagnostics() };
            }
            else if (scenario is "load-failure" or "first-frame-timeout")
            {
                int replacements = desktop.Replacements;
                ApplyGenerationResult failed = await Apply(scenario == "load-failure" ? bad.Definition.Id : good.Definition.Id);
                PreparedSession retained = host.GetPreparedSessionsForDiagnostics().Single();
                HostStateSnapshot retainedState = (await host.SendAsync(new GetHostStateCommand(), token)).State!;
                Require(failed.State == ApplyGenerationTerminalState.Failed && ReferenceEquals(retained.Renderer, renderer) &&
                    retained.Session.Id == active.Session.Id && desktop.Replacements == replacements && desktop.LiveCount == 1 &&
                    retainedState.Assignments.Single().WallpaperId == good.Definition.Id,
                    "Failed replacement did not preserve the old committed session or clean provisionals.");
                Require(failed.TerminalPhase == (scenario == "load-failure" ? ApplyGenerationPhase.ContentLoaded : ApplyGenerationPhase.FirstFramePresented), "Failure phase mismatch.");
                // Disable the diagnostic delivery filter: a subsequent user Apply must succeed in this Host.
                provider.Suppress = false;
                ApplyGenerationResult next = await Apply(good.Definition.Id);
                Require(next.State == ApplyGenerationTerminalState.Succeeded && desktop.Replacements == replacements + 1, "Apply after failure could not recover.");
                await host.WaitForPendingRetirementsForDiagnosticsAsync(token);
                expectedFailure = new { failed, oldSessionPreserved = true, failedGenerationReplacements = 0,
                    subsequentApply = next, injection = scenario == "first-frame-timeout" ? "HostDeliveryFilter/RealRendererFrameWithheld" : "RealInvalidMedia" };
            }
            else
            {
                Task destroyed = desktop.Destroyed(active.Surfaces.Single().Surface.Id);
                if (scenario == "process-exit") launcher.Records[0].KillForExperiment();
                else await renderer.DisconnectForDiagnosticsAsync();
                RendererFailed failed = await renderer.ActiveFailure.WaitAsync(token);
                await destroyed.WaitAsync(token); // Host observes failure and retires without a diagnostic command.
                await host.WaitForPendingRetirementsForDiagnosticsAsync(token);
                HostStateSnapshot state = (await host.SendAsync(new GetHostStateCommand(), token)).State!;
                Require(state.Sessions.Count == 0 && state.Assignments.Count == 1, "Active failure retained stale Surface or lost Assignment.");
                var failedRetirement = host.GetRetirementHistoryForDiagnostics().SelectMany(batch => batch.Sessions).Single();
                Require(!failedRetirement.ShutdownCompleted && failedRetirement.RendererDisposed && failedRetirement.SurfacesCleaned,
                    "Failed Renderer cleanup fabricated a ShutdownCompleted or leaked resources.");
                expectedFailure = new { failed, activeSessions = 0, assignmentPreserved = true, hostObservedWithoutInjection = true };
            }
        }
        finally
        {
            try
            {
                retirement = await host.RetireActiveSessionsForDiagnosticsAsync(CancellationToken.None);
                await host.WaitForPendingRetirementsForDiagnosticsAsync(CancellationToken.None);
            }
            finally
            {
                try { await host.DisposeAsync(); }
                catch (AggregateException exception) when (scenario is "process-exit" or "pipe-disconnect" &&
                    exception.Flatten().InnerExceptions.All(e => e is ActiveRendererFailureException))
                {
                    observedHostFailures = exception.Flatten().InnerExceptions.Cast<ActiveRendererFailureException>().ToArray();
                    Require(observedHostFailures.Length == 1, "Unexpected number of active Host failures.");
                }
            }
        }
        int[] exitCodes = launcher.Records.Select(record => record.ReadExitCode()).ToArray();
        Require(scenario is "process-exit" or "pipe-disconnect" ? exitCodes.Single() != 0 : exitCodes[0] == 0 && exitCodes[^1] == 0,
            "Unexpected real process return.");
        if (scenario == "load-failure") Require(exitCodes.Length == 3 && exitCodes[1] == 1, "Invalid media did not exit with a real failure.");
        Require(desktop.LiveCount == 0, "Owned Host windows remain.");
        return new { controls, expectedFailure, retirement, retirements = host.GetRetirementHistoryForDiagnostics(),
            observedHostFailures = observedHostFailures.Select(e => new { e.Generation, e.ErrorCode }),
            rendererExitCodes = exitCodes,
            processCleanup = launcher.Records.Select(record => new { record.HostTerminationRequests, record.HostHandleDisposals }),
            replacementCount = desktop.Replacements, allOwnedResourcesCleaned = true };

        async Task<ApplyGenerationResult> Apply(WallpaperId id)
        {
            var accepted = await host.SendAsync(new ApplyWallpaperCommand(id, [display], LayoutMode.PerDisplay, FitMode.Cover), token);
            Require(accepted.Accepted, "Apply rejected.");
            return await host.WaitForApplyGenerationAsync(accepted.State!.Generation, token);
        }
    }

    private static async Task Frame(RendererProtocolSession renderer, long generation, CancellationToken token)
    {
        await foreach (RendererEvent item in renderer.ReadEventsAsync(token))
        {
            if (item is RendererPlaybackStateChanged) continue;
            Require(item is RendererFirstFramePresented && item.Generation == generation, "Wrong bounds completion.");
            return;
        }
        throw new IOException("Missing bounds first frame.");
    }
    private static double Position(JsonElement e) => e.GetProperty("snapshot").GetProperty("position").GetDouble();
    private static JsonElement[] Read(string path)
    {
        if (!File.Exists(path)) return [];
        using StreamReader reader = new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        List<JsonElement> values = [];
        while (reader.ReadLine() is { } line)
        {
            if (!line.EndsWith('}')) break;
            using JsonDocument document = JsonDocument.Parse(line); values.Add(document.RootElement.Clone());
        }
        return values.ToArray();
    }
    private static async Task<JsonElement> Evidence(string path, string phase, int after, CancellationToken token)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        while (true)
        {
            foreach (var item in Read(path))
                if (item.GetProperty("phase").GetString() == phase && item.GetProperty("ordinal").GetInt32() > after) return item;
            await Task.Delay(10, timeout.Token);
        }
    }
    private static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }

    private sealed class Provider(RendererProcessProvider inner, bool suppress) : IRendererProvider, IObservableRendererProvider
    {
        private int launches;
        public bool Suppress { get; set; } = suppress;
        public RendererDescriptor Descriptor => inner.Descriptor;
        public RendererMatch Match(WallpaperDefinition definition, RuntimeEnvironment environment) => inner.Match(definition, environment);
        public Task<IRendererSession> CreateAsync(RendererLaunchContext context, CancellationToken token) => CreateObservedAsync(context, _ => { }, token);
        public async Task<IRendererSession> CreateObservedAsync(RendererLaunchContext context, Action<RendererProcessMilestone> observer, CancellationToken token)
        {
            IRendererSession session = await inner.CreateObservedAsync(context, observer, token);
            return ++launches == 2 && Suppress ? new FrameFilter(session) : session;
        }
    }
    private sealed class FrameFilter(IRendererSession inner) : IRendererSession
    {
        public SessionId Id => inner.Id;
        public Task SendAsync(RendererCommand command, CancellationToken token) => inner.SendAsync(command, token);
        public async IAsyncEnumerable<RendererEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken token)
        { await foreach (var item in inner.ReadEventsAsync(token)) if (item is not RendererFirstFramePresented) yield return item; }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private sealed class Repository(params WallpaperLibraryEntry[] entries) : IWallpaperRepository
    {
        public Task<WallpaperLibraryEntry?> FindAsync(WallpaperId id, CancellationToken token) => Task.FromResult(entries.FirstOrDefault(e => e.Definition.Id == id));
        public Task<IReadOnlyList<WallpaperLibraryEntry>> ListAsync(WallpaperQuery query, CancellationToken token) => Task.FromResult<IReadOnlyList<WallpaperLibraryEntry>>(entries);
        public Task SaveImportedAsync(ImportedWallpaper wallpaper, CancellationToken token) => throw new NotSupportedException();
        public Task RemoveAsync(WallpaperId id, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class Launcher : IRendererProcessLauncher, IDisposable
    {
        public List<Record> Records { get; } = [];
        public IRendererProcessHandle Start(RendererProcessStartRequest request)
        {
            var handle = new RendererProcessLauncher().Start(request);
            var process = Process.GetProcessById(handle.Id); _ = process.SafeHandle;
            Record record = new(process);
            Records.Add(record); return new TrackedHandle(handle, record);
        }
        public void Dispose() { foreach (var record in Records) record.Dispose(); }
    }
    private sealed class Record(Process process) : IDisposable
    {
        public int HostTerminationRequests { get; set; }
        public int HostHandleDisposals { get; set; }
        public void KillForExperiment() => process.Kill(entireProcessTree: true);
        public int ReadExitCode() { Require(process.WaitForExit(3000), "Renderer still running."); return process.ExitCode; }
        public void Dispose() { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(3000); } process.Dispose(); }
    }
    private sealed class TrackedHandle(IRendererProcessHandle inner, Record record) : IRendererProcessHandle
    {
        public int Id => inner.Id;
        public bool HasExited => inner.HasExited;
        public int? ExitCode => inner.ExitCode;
        public Task WaitForExitAsync(CancellationToken token) => inner.WaitForExitAsync(token);
        public void Terminate() { record.HostTerminationRequests++; inner.Terminate(); }
        public ValueTask DisposeAsync() { record.HostHandleDisposals++; return inner.DisposeAsync(); }
    }
    private sealed class HiddenDesktop : IDesktopHost, IDisposable
    {
        private readonly ConcurrentQueue<Action> owner = new();
        private readonly ConcurrentDictionary<SurfaceId, ProbeWindow> windows = new();
        private readonly ConcurrentDictionary<SurfaceId, TaskCompletionSource> destroyed = new();
        public int Replacements { get; private set; }
        public int LiveCount => windows.Count;
        public Task Destroyed(SurfaceId id) => destroyed[id].Task;
        private Task<T> OnOwner<T>(Func<T> action)
        {
            TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.Enqueue(() => { try { completion.TrySetResult(action()); } catch (Exception e) { completion.TrySetException(e); } });
            return completion.Task;
        }
        public void Pump() { while (owner.TryDequeue(out Action? work)) work(); foreach (var window in windows.Values) { window.Pump(); break; } }
        public Task<DesktopTopology> EnsureTopologyAsync(DisplayTopology displays, CancellationToken token) => Task.FromResult(new DesktopTopology(1, [new("hidden", "DirectComposition", "HwndChild")]));
        public Task<DesktopSurface> CreateSurfaceAsync(SurfaceRequest request, CancellationToken token) => OnOwner(() =>
        {
            SurfaceId id = new(Guid.NewGuid().ToString("N"));
            ProbeWindow window = ProbeWindow.CreateHiddenHost(request.Bounds.Width, request.Bounds.Height);
            windows[id] = window; destroyed[id] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new DesktopSurface(id, request.DisplayIds, unchecked((ulong)window.Handle.ToInt64()));
        });
        public Task ResizeSurfaceAsync(SurfaceId id, SurfaceRequest request, CancellationToken token) => OnOwner(() => { windows[id].Resize(request.Bounds.Width, request.Bounds.Height); return true; });
        public Task ReplaceSurfacesAsync(SurfaceReplacement replacement, CancellationToken token) { Replacements++; return Task.CompletedTask; }
        public Task DestroySurfaceAsync(SurfaceId id, CancellationToken token) => OnOwner(() =>
        {
            if (windows.TryRemove(id, out ProbeWindow? window)) window.Dispose();
            destroyed[id].TrySetResult(); return true;
        });
        public Task AbandonSurfaceAsync(SurfaceId id, CancellationToken token) => DestroySurfaceAsync(id, token);
        public void Dispose() { foreach (var id in windows.Keys) if (windows.TryRemove(id, out ProbeWindow? window)) window.Dispose(); }
    }
}
