using LiveWall.Application.Sessions;

namespace LiveWall.Host.Orchestration;

internal sealed class RendererProtocolStateMachine
{
    private RendererProtocolState state = RendererProtocolState.Initialized;
    private bool contentLoaded;

    public RendererProtocolStateMachine(long generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        Generation = generation;
    }

    public long Generation { get; private set; }

    public bool IsClosed => state == RendererProtocolState.Closed;

    public void ValidateAndApply(RendererCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (IsClosed)
        {
            throw new InvalidOperationException("The Renderer protocol session is closed.");
        }

        switch (command)
        {
            case AttachRendererSurface attach:
                bool initialAttach = state == RendererProtocolState.Initialized &&
                    attach.Generation == Generation;
                bool recoveryAttach = state == RendererProtocolState.Running &&
                    attach.Generation > Generation;
                if (!initialAttach && !recoveryAttach)
                {
                    throw InvalidCommand(command);
                }

                Generation = attach.Generation;
                state = RendererProtocolState.AwaitingSurfaceAttached;
                break;

            case LoadRendererContent load when
                state == RendererProtocolState.SurfaceReady && load.Generation == Generation:
                state = RendererProtocolState.AwaitingContentLoaded;
                break;

            case SetRendererBounds bounds when
                state == RendererProtocolState.Running && bounds.Generation >= Generation:
                break;

            case ChangeRendererState playback when
                state == RendererProtocolState.Running && playback.Generation == Generation:
            case SetRendererFit fit when
                state == RendererProtocolState.Running && fit.Generation == Generation:
            case SetRendererVolume volume when
                state == RendererProtocolState.Running && volume.Generation == Generation:
            case ThrottleRenderer throttle when
                state == RendererProtocolState.Running && throttle.Generation == Generation:
                break;

            case ShutdownRenderer shutdown when shutdown.Generation >= Generation:
                state = RendererProtocolState.ShuttingDown;
                break;

            default:
                throw InvalidCommand(command);
        }
    }

    public void ValidateAndApply(RendererEvent rendererEvent)
    {
        ArgumentNullException.ThrowIfNull(rendererEvent);
        if (rendererEvent.Generation != Generation)
        {
            throw new InvalidOperationException(
                $"Renderer event generation {rendererEvent.Generation} does not match current generation {Generation}.");
        }

        switch (rendererEvent)
        {
            case RendererSurfaceAttached when state == RendererProtocolState.AwaitingSurfaceAttached:
                state = contentLoaded
                    ? RendererProtocolState.AwaitingReattachFirstFrame
                    : RendererProtocolState.SurfaceReady;
                break;
            case RendererContentLoaded when state == RendererProtocolState.AwaitingContentLoaded:
                contentLoaded = true;
                state = RendererProtocolState.AwaitingInitialFirstFrame;
                break;
            case RendererFirstFramePresented when
                state is RendererProtocolState.AwaitingInitialFirstFrame or
                    RendererProtocolState.AwaitingReattachFirstFrame:
                state = RendererProtocolState.Running;
                break;
            case RendererPlaybackStateChanged when state == RendererProtocolState.Running:
            case RendererFailed:
                break;
            case RendererShutdownCompleted when state == RendererProtocolState.ShuttingDown:
                state = RendererProtocolState.Closed;
                break;
            default:
                throw new InvalidOperationException(
                    $"Renderer event {rendererEvent.GetType().Name} is invalid while protocol state is {state}.");
        }
    }

    public void Close() => state = RendererProtocolState.Closed;

    private InvalidOperationException InvalidCommand(RendererCommand command) =>
        new($"Renderer command {command.GetType().Name} generation {command.Generation} " +
            $"is invalid while protocol state is {state} at generation {Generation}.");

    private enum RendererProtocolState
    {
        Initialized,
        AwaitingSurfaceAttached,
        SurfaceReady,
        AwaitingContentLoaded,
        AwaitingInitialFirstFrame,
        AwaitingReattachFirstFrame,
        Running,
        ShuttingDown,
        Closed,
    }
}
