using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LiveWall.Contracts.RendererProtocol;
using LiveWall.Infrastructure.FileSystem;
using LiveWall.Infrastructure.Ipc;

namespace LiveWall.VideoPresentationProbe;

// Real product executable + private hidden containers. No Shell sampling/mutation or desktop display.
internal static class VideoProtocolPreflight
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static int Run(string renderer, string library, string root, string media, string output, string scenario)
    {
        if (File.Exists(output)) throw new IOException("Use a fresh protocol result path.");
        if (scenario is not ("normal" or "shutdown-after-attach" or "disconnect-after-attach" or "invalid-path"))
            throw new ArgumentException("Unknown protocol preflight scenario.", nameof(scenario));
        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(40));
        using ProbeWindow first = ProbeWindow.CreateHiddenHost(320, 180);
        using ProbeWindow second = ProbeWindow.CreateHiddenHost(640, 360);
        Task<Outcome> run = Task.Run(() => RunAsync(renderer, library, root, media,
            unchecked((ulong)first.Handle.ToInt64()), unchecked((ulong)second.Handle.ToInt64()), scenario, budget.Token));
        while (!run.IsCompleted) { first.Pump(); Thread.Sleep(2); }
        Outcome result = run.GetAwaiter().GetResult();
        first.Dispose(); second.Dispose();
        var evidence = new { schemaVersion = 1, scope = "C1FormalVideoProtocolHiddenContainer", recordedAt = DateTimeOffset.UtcNow,
            scenario, result.Valid, result.Failure, result.Events, result.ChildChecks, result.FirstFrameCount,
            result.PausedReattachAcknowledged, result.RendererExitCode, result.ForcedExit,
            result.ShutdownCompleted, result.RendererChildrenCleaned,
            hostWindowsCleaned = first.Handle == 0 && second.Handle == 0,
            shellTouched = false, desktopPresentationValidated = false, productionAttachmentEnabled = false,
            expectedFailure = scenario is "disconnect-after-attach" or "invalid-path" };
        AtomicFileWriter.WriteJsonAsync(Path.GetFullPath(output), evidence).GetAwaiter().GetResult();
        Console.WriteLine($"C1_PROTOCOL_PREFLIGHT: valid={result.Valid}; scenario={scenario}; result={Path.GetFullPath(output)}");
        return result.Valid ? 0 : 1;
    }

    private static async Task<Outcome> RunAsync(string renderer, string library, string root, string media,
        ulong first, ulong second, string scenario, CancellationToken token)
    {
        RendererConnectionCredentials credentials = RendererConnectionCredentials.Create("videoc1");
        using var pipe = LocalNamedPipeFactory.CreateServer(credentials.PipeName, 1, true);
        await using LengthPrefixedJsonChannel channel = new(pipe, leaveOpen: true);
        string session = Guid.NewGuid().ToString("N");
        ProcessStartInfo start = new(renderer) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(credentials.PipeName); start.ArgumentList.Add(session);
        start.Environment["LIVEWALL_RENDERER_AUTH_TOKEN"] = credentials.AuthenticationToken;
        start.Environment["LIVEWALL_VIDEO_LIBMPV_PATH"] = Path.GetFullPath(library);
        using Process process = Process.Start(start) ?? throw new IOException("Video process could not start.");
        Task<string> stderr = process.StandardError.ReadToEndAsync(token);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(token);
        List<string> events = [];
        List<ulong> children = [];
        HashSet<string> ids = new(StringComparer.Ordinal);
        string? failure = null;
        bool childChecks = true, shutdown = false, pausedReattach = false, expectedError = false, forced = false;
        int firstFrames = 0;
        try
        {
            await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
            RendererEnvelope hello = await Expect(RendererEventTypes.Hello, 0).ConfigureAwait(false);
            HelloPayload greeting = hello.Payload.Deserialize<HelloPayload>(Json)!;
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(greeting.AuthenticationToken),
                    System.Text.Encoding.UTF8.GetBytes(credentials.AuthenticationToken))) throw new InvalidDataException("Hello authentication failed.");
            await Send(RendererCommandTypes.Initialize, new InitializePayload("video", ["HwndChild", "DirectComposition"], "zh-CN"), 1).ConfigureAwait(false);
            await Expect(RendererEventTypes.Initialized, 1).ConfigureAwait(false);
            await Send(RendererCommandTypes.AttachSurface, new AttachSurfacePayload(first, 320, 180, 1), 1).ConfigureAwait(false);
            ulong child = await Attached(1).ConfigureAwait(false);
            childChecks &= ProbeWindow.ChildMatches(child, first, process.Id, 320, 180);
            if (scenario == "disconnect-after-attach")
            {
                pipe.Dispose(); expectedError = true;
            }
            else
            {
                if (scenario != "shutdown-after-attach")
                {
                    await Send(RendererCommandTypes.LoadWallpaper,
                        new LoadWallpaperPayload("c1-motion", "Video", Path.GetFullPath(root),
                            scenario == "invalid-path" ? "../escape.avi" : Path.GetRelativePath(root, media),
                            new Dictionary<string, JsonElement>()), 1).ConfigureAwait(false);
                    if (scenario == "invalid-path")
                    {
                        RendererEnvelope rejection = await Expect(RendererEventTypes.FatalError, 1).ConfigureAwait(false);
                        if (rejection.Payload.GetProperty("code").GetString() != RendererErrorCodes.ContentInvalidPath)
                            throw new InvalidDataException("Expected invalid content path rejection.");
                        expectedError = true;
                    }
                    else
                    {
                        await Expect(RendererEventTypes.ContentLoaded, 1).ConfigureAwait(false);
                        await Expect(RendererEventTypes.FirstFramePresented, 1).ConfigureAwait(false);
                        await Send(RendererCommandTypes.Pause, new EmptyPayload(), 1).ConfigureAwait(false);
                        RendererEnvelope paused = await Expect(RendererEventTypes.PlaybackStateChanged, 1).ConfigureAwait(false);
                        if (paused.Payload.GetProperty("state").GetString() != "Paused") throw new InvalidDataException("Pause was not confirmed.");
                        await Send(RendererCommandTypes.SetBounds, new SetBoundsPayload(0, 0, 640, 360, 1), 2).ConfigureAwait(false);
                        await Expect(RendererEventTypes.FirstFramePresented, 1).ConfigureAwait(false);
                        childChecks &= ProbeWindow.ChildMatches(child, first, process.Id, 640, 360);
                        await Send(RendererCommandTypes.AttachSurface, new AttachSurfacePayload(second, 640, 360, 1), 2).ConfigureAwait(false);
                        ulong replacement = await Attached(2).ConfigureAwait(false);
                        childChecks &= replacement != child && !ProbeWindow.WindowExists(child) &&
                            ProbeWindow.ChildMatches(replacement, second, process.Id, 640, 360);
                        await Expect(RendererEventTypes.FirstFramePresented, 2).ConfigureAwait(false);
                        pausedReattach = true;
                        await Send(RendererCommandTypes.Resume, new EmptyPayload(), 2).ConfigureAwait(false);
                        await Expect(RendererEventTypes.PlaybackStateChanged, 2).ConfigureAwait(false);
                    }
                }
                if (!expectedError)
                {
                    long generation = scenario == "normal" ? 2 : 1;
                    await Send(RendererCommandTypes.Shutdown, new ShutdownPayload("preflight-complete"), generation).ConfigureAwait(false);
                    await Expect(RendererEventTypes.ShutdownCompleted, generation).ConfigureAwait(false);
                    shutdown = true;
                    childChecks &= children.All(window => !ProbeWindow.WindowExists(window));
                }
            }
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (Exception exception) { failure = exception.GetType().Name + ": " + exception.Message; }
        finally
        {
            if (!process.HasExited)
            {
                using CancellationTokenSource exitBudget = new(TimeSpan.FromSeconds(3));
                try { await process.WaitForExitAsync(exitBudget.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { process.Kill(entireProcessTree: true); forced = true; await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            }
        }
        bool cleaned = children.All(window => !ProbeWindow.WindowExists(window));
        string errors = await stderr.ConfigureAwait(false);
        _ = await stdout.ConfigureAwait(false);
        bool valid = failure is null && childChecks && cleaned && !forced &&
            (expectedError ? process.ExitCode == 1 : process.ExitCode == 0 && shutdown && errors.Length == 0) &&
            (scenario == "normal" ? firstFrames == 3 && pausedReattach : firstFrames == 0);
        return new(valid, failure, events, childChecks, firstFrames, pausedReattach, process.ExitCode, forced, shutdown, cleaned);

        async Task Send<T>(string type, T payload, long generation) =>
            await channel.WriteAsync(new RendererEnvelope(ProtocolVersion.Version1, Guid.NewGuid().ToString("N"), session, generation,
                type, JsonSerializer.SerializeToElement(payload, Json)), token).ConfigureAwait(false);
        async Task<RendererEnvelope> Expect(string type, long generation)
        {
            RendererEnvelope envelope = await channel.ReadAsync<RendererEnvelope>(token).ConfigureAwait(false);
            if (envelope.Protocol.Major != 1 || envelope.SessionId != session || envelope.Generation != generation ||
                envelope.Type != type || !ids.Add(envelope.MessageId)) throw new InvalidDataException($"Expected {type}; received {envelope.Type}.");
            events.Add($"{envelope.Generation}:{envelope.Type}");
            if (type == RendererEventTypes.FirstFramePresented) firstFrames++;
            return envelope;
        }
        async Task<ulong> Attached(long generation)
        {
            RendererEnvelope attached = await Expect(RendererEventTypes.SurfaceAttached, generation).ConfigureAwait(false);
            ulong window = attached.Payload.GetProperty("windowHandle").GetUInt64(); children.Add(window); return window;
        }
    }
    private sealed record Outcome(bool Valid, string? Failure, IReadOnlyList<string> Events, bool ChildChecks,
        int FirstFrameCount, bool PausedReattachAcknowledged, int RendererExitCode, bool ForcedExit,
        bool ShutdownCompleted, bool RendererChildrenCleaned);
}
