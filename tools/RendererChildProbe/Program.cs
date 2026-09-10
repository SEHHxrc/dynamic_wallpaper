using System.Text.Json;
using LiveWall.Contracts.RendererProtocol;
using LiveWall.Diagnostics;
using LiveWall.Infrastructure.Ipc;
using LiveWall.Platform.Windows.Desktop;

namespace LiveWall.RendererChildProbe;

internal static class Program
{
    private static readonly uint[] ProbeColors = [0x000080FF, 0x0000FFFF];
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly string[] SupportedCapabilities = ["HwndChild", "DirectComposition"];
    private const int InjectedFatalExitCode = 41;
    private const int InjectedProcessExitCode = 42;

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("RendererChildProbe requires pipe name and session id.");
            return 2;
        }

        string pipeName = args[0];
        string sessionId = args[1];
        string token = Environment.GetEnvironmentVariable("LIVEWALL_RENDERER_AUTH_TOKEN") ??
            throw new InvalidOperationException("Renderer authentication token was not supplied.");
        Environment.SetEnvironmentVariable("LIVEWALL_RENDERER_AUTH_TOKEN", null);
        ProbeFaultPlan? faultPlan = ProbeFaultPlan.ReadAndClearFromEnvironment();
        try
        {
            await using System.IO.Pipes.NamedPipeClientStream pipe =
                LocalNamedPipeFactory.CreateClient(pipeName);
            await pipe.ConnectAsync(timeout: 10_000).ConfigureAwait(false);
            await using LengthPrefixedJsonChannel channel = new(pipe);
            await channel.WriteAsync(CreateEnvelope(
                    sessionId,
                    generation: 0,
                    RendererEventTypes.Hello,
                    new HelloPayload(
                        "renderer-child-dcomp-probe",
                        "1.0.0",
                        SupportedCapabilities,
                        token)))
                .ConfigureAwait(false);

            try
            {
                return await RunProtocolAsync(channel, sessionId, faultPlan).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await TrySendFatalErrorAsync(channel, sessionId, exception).ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Renderer-child probe failed: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunProtocolAsync(
        LengthPrefixedJsonChannel channel,
        string sessionId,
        ProbeFaultPlan? faultPlan)
    {
        using CancellationTokenSource handshakeTimeout = new(TimeSpan.FromSeconds(10));
        RendererEnvelope initialize = await channel
            .ReadAsync<RendererEnvelope>(handshakeTimeout.Token)
            .ConfigureAwait(false);
        ValidateEnvelope(initialize, sessionId, RendererCommandTypes.Initialize);
        InitializePayload initializePayload = ReadPayload<InitializePayload>(initialize);
        string[] unsupported = initializePayload.RequestedCapabilities
            .Except(SupportedCapabilities, StringComparer.Ordinal)
            .ToArray();
        if (unsupported.Length > 0)
        {
            throw new InvalidOperationException(
                $"Unsupported requested capabilities: {string.Join(", ", unsupported)}.");
        }

        HashSet<string> commandIds = new(StringComparer.Ordinal) { initialize.MessageId };
        await channel.WriteAsync(CreateEnvelope(
                sessionId,
                initialize.Generation,
                RendererEventTypes.Initialized,
                new InitializedPayload(initializePayload.RequestedCapabilities)))
            .ConfigureAwait(false);

        RendererChildDirectCompositionSession? graphics = null;
        long currentGeneration = initialize.Generation;
        bool hasAttached = false;
        bool contentLoaded = false;
        bool faultConsumed = false;
        try
        {
            while (true)
            {
                RendererEnvelope command = await channel.ReadAsync<RendererEnvelope>()
                    .ConfigureAwait(false);
                ValidateEnvelope(command, sessionId);
                if (!commandIds.Add(command.MessageId))
                {
                    throw new InvalidOperationException(
                        $"Host reused messageId '{command.MessageId}'.");
                }

                switch (command.Type)
                {
                    case RendererCommandTypes.Shutdown:
                        if (graphics is not null)
                        {
                            await graphics.DisposeAsync().ConfigureAwait(false);
                            graphics = null;
                        }
                        ProbeFaultDisposition shutdownFault = await ApplyFaultAsync(
                                channel,
                                sessionId,
                                command.Generation,
                                faultPlan,
                                ProbeFaultPhase.ShutdownCompleted,
                                faultConsumed)
                            .ConfigureAwait(false);
                        faultConsumed |= shutdownFault != ProbeFaultDisposition.None;
                        if (shutdownFault == ProbeFaultDisposition.Suppress)
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                        }
                        if (shutdownFault == ProbeFaultDisposition.Fatal)
                        {
                            return InjectedFatalExitCode;
                        }
                        if (shutdownFault == ProbeFaultDisposition.Exit)
                        {
                            return InjectedProcessExitCode;
                        }
                        await channel.WriteAsync(CreateEnvelope(
                                sessionId,
                                command.Generation,
                                RendererEventTypes.ShutdownCompleted,
                                new ShutdownCompletedPayload(
                                    "Renderer-child probe resources released.")))
                            .ConfigureAwait(false);
                        return 0;

                    case RendererCommandTypes.SetBounds:
                        if (!contentLoaded || command.Generation < currentGeneration)
                        {
                            throw InvalidState(command, "SetBounds requires loaded content and a current or newer generation.");
                        }
                        _ = ReadPayload<SetBoundsPayload>(command);
                        break;

                    case RendererCommandTypes.AttachSurface:
                        bool validInitial = !hasAttached && command.Generation == currentGeneration;
                        bool validRecovery = hasAttached && contentLoaded &&
                            command.Generation > currentGeneration;
                        if (!validInitial && !validRecovery)
                        {
                            throw InvalidState(command, "Attach generation is duplicate, stale, or out of order.");
                        }

                        AttachSurfacePayload attach = ReadPayload<AttachSurfacePayload>(command);
                        if (graphics is not null)
                        {
                            await graphics.DisposeAsync().ConfigureAwait(false);
                        }
                        graphics = await RendererChildDirectCompositionSession.CreateAsync(
                                attach.WindowHandle,
                                attach.Width,
                                attach.Height,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        currentGeneration = command.Generation;
                        hasAttached = true;
                        ProbeFaultDisposition attachFault = await ApplyFaultAsync(
                                channel,
                                sessionId,
                                currentGeneration,
                                faultPlan,
                                ProbeFaultPhase.SurfaceAttached,
                                faultConsumed)
                            .ConfigureAwait(false);
                        faultConsumed |= attachFault != ProbeFaultDisposition.None;
                        if (attachFault == ProbeFaultDisposition.Suppress)
                        {
                            break;
                        }
                        if (attachFault == ProbeFaultDisposition.Fatal)
                        {
                            return InjectedFatalExitCode;
                        }
                        if (attachFault == ProbeFaultDisposition.Exit)
                        {
                            return InjectedProcessExitCode;
                        }
                        await channel.WriteAsync(CreateEnvelope(
                                sessionId,
                                currentGeneration,
                                RendererEventTypes.SurfaceAttached,
                                new SurfaceAttachedPayload(graphics.WindowHandle)))
                            .ConfigureAwait(false);
                        if (contentLoaded)
                        {
                            await PresentAndReportAsync(
                                    channel,
                                    graphics,
                                    sessionId,
                                    currentGeneration)
                                .ConfigureAwait(false);
                        }
                        break;

                    case RendererCommandTypes.LoadWallpaper:
                        if (!hasAttached || graphics is null || contentLoaded ||
                            command.Generation != currentGeneration)
                        {
                            throw InvalidState(command, "LoadWallpaper requires a completed initial AttachSurface.");
                        }

                        LoadWallpaperPayload load = ReadPayload<LoadWallpaperPayload>(command);
                        contentLoaded = true;
                        ProbeFaultDisposition contentFault = await ApplyFaultAsync(
                                channel,
                                sessionId,
                                currentGeneration,
                                faultPlan,
                                ProbeFaultPhase.ContentLoaded,
                                faultConsumed)
                            .ConfigureAwait(false);
                        faultConsumed |= contentFault != ProbeFaultDisposition.None;
                        if (contentFault == ProbeFaultDisposition.Suppress)
                        {
                            break;
                        }
                        if (contentFault == ProbeFaultDisposition.Fatal)
                        {
                            return InjectedFatalExitCode;
                        }
                        if (contentFault == ProbeFaultDisposition.Exit)
                        {
                            return InjectedProcessExitCode;
                        }
                        await channel.WriteAsync(CreateEnvelope(
                                sessionId,
                                currentGeneration,
                                RendererEventTypes.ContentLoaded,
                                new ContentLoadedPayload(load.WallpaperId)))
                            .ConfigureAwait(false);
                        ProbeFaultDisposition firstFrameFault = await ApplyFaultAsync(
                                channel,
                                sessionId,
                                currentGeneration,
                                faultPlan,
                                ProbeFaultPhase.FirstFramePresented,
                                faultConsumed)
                            .ConfigureAwait(false);
                        faultConsumed |= firstFrameFault != ProbeFaultDisposition.None;
                        if (firstFrameFault == ProbeFaultDisposition.Suppress)
                        {
                            break;
                        }
                        if (firstFrameFault == ProbeFaultDisposition.Fatal)
                        {
                            return InjectedFatalExitCode;
                        }
                        if (firstFrameFault == ProbeFaultDisposition.Exit)
                        {
                            return InjectedProcessExitCode;
                        }
                        await PresentAndReportAsync(
                                channel,
                                graphics,
                                sessionId,
                                currentGeneration)
                            .ConfigureAwait(false);
                        break;

                    default:
                        throw InvalidState(command, "The command is unknown or unsupported by the test Renderer.");
                }
            }
        }
        finally
        {
            if (graphics is not null)
            {
                await graphics.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<ProbeFaultDisposition> ApplyFaultAsync(
        LengthPrefixedJsonChannel channel,
        string sessionId,
        long generation,
        ProbeFaultPlan? plan,
        ProbeFaultPhase phase,
        bool faultConsumed)
    {
        if (plan is null || faultConsumed || plan.Phase != phase)
        {
            return ProbeFaultDisposition.None;
        }

        if (plan.Action == ProbeFaultAction.FatalEvent)
        {
            await channel.WriteAsync(CreateEnvelope(
                    sessionId,
                    generation,
                    RendererEventTypes.FatalError,
                    new RendererErrorPayload(
                        $"renderer.probe.injected.{phase}",
                        $"Injected Renderer probe fault before {phase}.",
                        Retryable: false,
                        Details: null)))
                .ConfigureAwait(false);
            return ProbeFaultDisposition.Fatal;
        }

        return plan.Action switch
        {
            ProbeFaultAction.ExitProcess => ProbeFaultDisposition.Exit,
            ProbeFaultAction.SuppressEvent => ProbeFaultDisposition.Suppress,
            _ => throw new ArgumentOutOfRangeException(nameof(plan)),
        };
    }

    private static async Task PresentAndReportAsync(
        LengthPrefixedJsonChannel channel,
        RendererChildDirectCompositionSession graphics,
        string sessionId,
        long generation)
    {
        uint color = ProbeColors[(int)((generation - 1) % ProbeColors.Length)];
        await graphics.PresentAsync(color, CancellationToken.None).ConfigureAwait(false);
        await channel.WriteAsync(CreateEnvelope(
                sessionId,
                generation,
                RendererEventTypes.FirstFramePresented,
                new FirstFramePresentedPayload(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())))
            .ConfigureAwait(false);
    }

    private static async Task TrySendFatalErrorAsync(
        LengthPrefixedJsonChannel channel,
        string sessionId,
        Exception exception)
    {
        try
        {
            await channel.WriteAsync(CreateEnvelope(
                    sessionId,
                    generation: 0,
                    RendererEventTypes.FatalError,
                    new RendererErrorPayload(
                        "renderer.protocol.invalid_state",
                        exception.Message,
                        Retryable: false,
                        Details: null)))
                .ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static RendererEnvelope CreateEnvelope<T>(
        string sessionId,
        long generation,
        string type,
        T payload) =>
        new(
            ProtocolVersion.Version1,
            Guid.NewGuid().ToString("N"),
            sessionId,
            generation,
            type,
            JsonSerializer.SerializeToElement(payload, SerializerOptions));

    private static T ReadPayload<T>(RendererEnvelope envelope) =>
        envelope.Payload.Deserialize<T>(SerializerOptions) ??
        throw new InvalidOperationException($"{envelope.Type} payload was empty.");

    private static void ValidateEnvelope(
        RendererEnvelope envelope,
        string sessionId,
        string? expectedType = null)
    {
        if (envelope.Protocol.Major != ProtocolVersion.Version1.Major ||
            envelope.SessionId != sessionId ||
            string.IsNullOrWhiteSpace(envelope.MessageId) ||
            (expectedType is not null && envelope.Type != expectedType))
        {
            throw new InvalidOperationException(
                $"Unexpected Renderer command: protocol={envelope.Protocol.Major}.{envelope.Protocol.Minor}, " +
                $"session={envelope.SessionId}, type={envelope.Type}.");
        }
    }

    private static InvalidOperationException InvalidState(
        RendererEnvelope command,
        string reason) =>
        new($"Command {command.Type} generation {command.Generation} was rejected: {reason}");

    private enum ProbeFaultDisposition
    {
        None,
        Fatal,
        Exit,
        Suppress,
    }
}
