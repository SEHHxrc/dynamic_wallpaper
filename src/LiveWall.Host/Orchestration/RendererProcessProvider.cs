using System.Diagnostics;
using System.IO.Pipes;
using LiveWall.Application.Sessions;
using LiveWall.Contracts.RendererProtocol;
using LiveWall.Domain.Sessions;
using LiveWall.Domain.Wallpapers;
using LiveWall.Infrastructure.Ipc;

namespace LiveWall.Host.Orchestration;

internal sealed record RendererProcessProviderOptions(
    RendererDescriptor Descriptor,
    string ExecutablePath,
    string InstallationScope,
    IReadOnlyList<string> RequestedCapabilities,
    string Locale,
    TimeSpan HandshakeTimeout);

internal sealed class RendererProcessProvider : IRendererProvider, IObservableRendererProvider
{
    private readonly RendererProcessProviderOptions options;
    private readonly IRendererProcessLauncher processLauncher;
    private readonly Func<RendererLaunchContext, int, int, IReadOnlyDictionary<string, string>?>?
        processEnvironmentFactory;
    private readonly Lock launchSequenceGate = new();
    private readonly Dictionary<long, (int Iteration, int NextSurfaceOrdinal)> launchSequences = [];
    private int nextIteration;

    public RendererProcessProvider(
        RendererProcessProviderOptions options,
        IRendererProcessLauncher? processLauncher = null,
        Func<RendererLaunchContext, int, int, IReadOnlyDictionary<string, string>?>?
            processEnvironmentFactory = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExecutablePath);
        LiveWall.Contracts.Ipc.IpcEndpointNames.ValidateInstallationScope(options.InstallationScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Locale);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            options.HandshakeTimeout,
            TimeSpan.Zero);
        this.processLauncher = processLauncher ?? new RendererProcessLauncher();
        this.processEnvironmentFactory = processEnvironmentFactory;
    }

    public RendererDescriptor Descriptor => options.Descriptor;

    public RendererMatch Match(WallpaperDefinition wallpaper, RuntimeEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(wallpaper);
        ArgumentNullException.ThrowIfNull(environment);
        bool supported = Descriptor.SupportedKinds.Contains(wallpaper.Kind);
        return new RendererMatch(
            supported,
            supported ? 100 : 0,
            supported
                ? $"{Descriptor.Name} supports {wallpaper.Kind}."
                : $"{Descriptor.Name} does not support {wallpaper.Kind}.");
    }

    public async Task<IRendererSession> CreateAsync(
        RendererLaunchContext context,
        CancellationToken cancellationToken) =>
        await CreateCoreAsync(context, null, cancellationToken).ConfigureAwait(false);

    public async Task<IRendererSession> CreateObservedAsync(
        RendererLaunchContext context,
        Action<RendererProcessMilestone> observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return await CreateCoreAsync(context, observer, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IRendererSession> CreateCoreAsync(
        RendererLaunchContext context,
        Action<RendererProcessMilestone>? observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ApplyGenerationPhase currentPhase = ApplyGenerationPhase.ProcessStarted;
        RendererConnectionCredentials credentials =
            RendererConnectionCredentials.Create(options.InstallationScope);
        using OneTimeAuthenticationToken authentication =
            new(credentials.AuthenticationToken);
        NamedPipeServerStream? pipe = LocalNamedPipeFactory.CreateServer(
            credentials.PipeName,
            maximumServerInstances: 1,
            isFirstInstance: true);
        LengthPrefixedJsonChannel? channel = null;
        IRendererProcessHandle? process = null;
        try
        {
            ReportStart(observer, ApplyGenerationPhase.ProcessStarted);
            (int iteration, int surfaceOrdinal) = NextLaunchCoordinates(context.Generation);
            process = processLauncher.Start(new RendererProcessStartRequest(
                options.ExecutablePath,
                credentials.PipeName,
                context.SessionId.Value,
                credentials.AuthenticationToken,
                processEnvironmentFactory?.Invoke(context, iteration, surfaceOrdinal)));
            Report(observer, ApplyGenerationPhase.ProcessStarted);

            using CancellationTokenSource handshake =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshake.CancelAfter(options.HandshakeTimeout);
            currentPhase = ApplyGenerationPhase.PipeConnected;
            ReportStart(observer, currentPhase);
            await pipe.WaitForConnectionAsync(handshake.Token).ConfigureAwait(false);
            Report(observer, ApplyGenerationPhase.PipeConnected);
            channel = new LengthPrefixedJsonChannel(pipe);
            pipe = null;

            currentPhase = ApplyGenerationPhase.HelloReceived;
            ReportStart(observer, currentPhase);
            RendererEnvelope hello = await channel.ReadAsync<RendererEnvelope>(handshake.Token)
                .ConfigureAwait(false);
            HelloPayload helloPayload = ValidateHello(hello, context, authentication);
            ValidateCapabilities(helloPayload.SupportedCapabilities);
            Report(observer, ApplyGenerationPhase.HelloReceived);

            currentPhase = ApplyGenerationPhase.Initialized;
            ReportStart(observer, currentPhase);
            await channel.WriteAsync(
                    RendererProtocolMapper.CreateEnvelope(
                        context.SessionId.Value,
                        context.Generation,
                        RendererCommandTypes.Initialize,
                        new InitializePayload(
                            Descriptor.Id.Value,
                            options.RequestedCapabilities,
                            options.Locale)),
                    handshake.Token)
                .ConfigureAwait(false);

            RendererEnvelope initialized = await channel
                .ReadAsync<RendererEnvelope>(handshake.Token)
                .ConfigureAwait(false);
            ValidateInitialized(initialized, context);
            if (StringComparer.Ordinal.Equals(hello.MessageId, initialized.MessageId))
            {
                throw new InvalidOperationException(
                    $"Renderer reused handshake messageId '{hello.MessageId}'.");
            }
            InitializedPayload initializedPayload =
                RendererProtocolMapper.ReadPayload<InitializedPayload>(initialized);
            ValidateCapabilities(initializedPayload.EnabledCapabilities);
            Report(observer, ApplyGenerationPhase.Initialized);

            RendererProtocolSession session = new(
                context.SessionId,
                context.Generation,
                channel,
                process,
                [hello.MessageId, initialized.MessageId]);
            channel = null;
            process = null;
            return session;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            TimeoutException timeout = new(
                $"Renderer handshake did not complete within {options.HandshakeTimeout}.",
                exception);
            if (observer is null)
            {
                throw timeout;
            }

            ApplyFailureReason reason = currentPhase == ApplyGenerationPhase.PipeConnected
                ? ApplyFailureReason.PipeConnectionFailed
                : ApplyFailureReason.InitializationFailed;
            Report(observer, currentPhase, ApplyStageOutcome.Timeout, reason, timeout.Message);
            throw new ApplyStageException(currentPhase, reason, timeout.Message, timeout);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (observer is not null && exception is not ApplyStageException)
        {
            ApplyFailureReason reason = currentPhase switch
            {
                ApplyGenerationPhase.ProcessStarted => ApplyFailureReason.ProcessStartFailed,
                ApplyGenerationPhase.PipeConnected => ApplyFailureReason.PipeConnectionFailed,
                ApplyGenerationPhase.HelloReceived => ApplyFailureReason.HelloInvalid,
                ApplyGenerationPhase.Initialized => ApplyFailureReason.InitializationFailed,
                _ => ApplyFailureReason.Unexpected,
            };
            Report(observer, currentPhase, ApplyStageOutcome.Rejected, reason, exception.Message);
            throw new ApplyStageException(currentPhase, reason, exception.Message, exception);
        }
        finally
        {
            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
            else if (pipe is not null)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }

            if (process is not null)
            {
                if (!process.HasExited)
                {
                    process.Terminate();
                }

                await process.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private (int Iteration, int SurfaceOrdinal) NextLaunchCoordinates(long generation)
    {
        lock (launchSequenceGate)
        {
            if (!launchSequences.TryGetValue(generation, out var sequence))
            {
                sequence = (++nextIteration, 0);
            }

            sequence.NextSurfaceOrdinal++;
            launchSequences[generation] = sequence;
            return (sequence.Iteration, sequence.NextSurfaceOrdinal);
        }
    }

    private static void Report(
        Action<RendererProcessMilestone>? observer,
        ApplyGenerationPhase phase,
        ApplyStageOutcome outcome = ApplyStageOutcome.Success,
        ApplyFailureReason reason = ApplyFailureReason.None,
        string? detail = null) =>
        observer?.Invoke(new RendererProcessMilestone(
            phase,
            outcome,
            reason,
            detail,
            Stopwatch.GetTimestamp()));

    private static void ReportStart(
        Action<RendererProcessMilestone>? observer,
        ApplyGenerationPhase phase) =>
        observer?.Invoke(new RendererProcessMilestone(
            phase,
            ApplyStageOutcome.Success,
            ApplyFailureReason.None,
            null,
            Stopwatch.GetTimestamp(),
            IsPhaseStart: true));

    private static HelloPayload ValidateHello(
        RendererEnvelope hello,
        RendererLaunchContext context,
        OneTimeAuthenticationToken authentication)
    {
        ValidateHandshakeEnvelope(hello, context.SessionId, generation: 0, RendererEventTypes.Hello);
        HelloPayload payload = RendererProtocolMapper.ReadPayload<HelloPayload>(hello);
        if (!authentication.TryConsume(payload.AuthenticationToken))
        {
            throw new InvalidOperationException("Renderer Hello authentication failed.");
        }

        return payload;
    }

    private static void ValidateInitialized(
        RendererEnvelope initialized,
        RendererLaunchContext context) =>
        ValidateHandshakeEnvelope(
            initialized,
            context.SessionId,
            context.Generation,
            RendererEventTypes.Initialized);

    private static void ValidateHandshakeEnvelope(
        RendererEnvelope envelope,
        SessionId sessionId,
        long generation,
        string expectedType)
    {
        if (envelope.Protocol.Major != ProtocolVersion.Version1.Major ||
            !StringComparer.Ordinal.Equals(envelope.SessionId, sessionId.Value) ||
            envelope.Generation != generation ||
            !StringComparer.Ordinal.Equals(envelope.Type, expectedType) ||
            string.IsNullOrWhiteSpace(envelope.MessageId))
        {
            throw new InvalidOperationException(
                $"Invalid Renderer handshake message; expected {expectedType} for " +
                $"session {sessionId.Value}, generation {generation}.");
        }
    }

    private void ValidateCapabilities(IReadOnlyList<string> capabilities)
    {
        string[] missing = options.RequestedCapabilities
            .Except(capabilities, StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Renderer did not negotiate required capabilities: {string.Join(", ", missing)}.");
        }
    }
}
