using LiveWall.Contracts.RendererProtocol;

namespace LiveWall.Renderer.Video.Protocol;

internal enum VideoSessionPhase
{
    Created, Initializing, Initialized, Attaching, SurfaceReady, Loading,
    AwaitingInitialFrame, AwaitingReattachFrame, AwaitingBoundsFrame, Running, Stopping, Stopped, Faulted,
}

// Single-writer transaction core. Not a transport or a completed product session.
// Completion methods are called only after the corresponding operation/event succeeds.
internal sealed class VideoSessionTransaction
{
    private readonly string sessionId;
    private Guid bindingEpoch;
    private long contentRevision;
    private long operation;
    private long lastAcceptedSequence;
    private int width;
    private int height;
    public VideoSessionTransaction(string sessionId, long generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sessionId.Length, 64);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        this.sessionId = sessionId;
        Generation = generation;
    }
    public long Generation { get; private set; }
    public VideoSessionPhase Phase { get; private set; }
    public VideoFrameTicket? PendingFrame { get; private set; }

    public void BeginInitialize()
    {
        Require(VideoSessionPhase.Created);
        Phase = VideoSessionPhase.Initializing;
    }
    public void Initialized()
    {
        Require(VideoSessionPhase.Initializing);
        Phase = VideoSessionPhase.Initialized;
    }
    public void BeginAttach(long generation, AttachSurfacePayload surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        bool initial = Phase == VideoSessionPhase.Initialized && generation == Generation;
        bool recovery = Phase == VideoSessionPhase.Running && generation > Generation;
        if (!initial && !recovery) throw InvalidState();
        ValidateBounds(surface.Width, surface.Height, surface.ScaleFactor);
        ArgumentOutOfRangeException.ThrowIfZero(surface.WindowHandle);
        Generation = generation;
        width = surface.Width; height = surface.Height;
        bindingEpoch = Guid.NewGuid();
        PendingFrame = null;
        Phase = VideoSessionPhase.Attaching;
    }
    public void SurfaceAttached()
    {
        Require(VideoSessionPhase.Attaching);
        if (contentRevision == 0) Phase = VideoSessionPhase.SurfaceReady;
        else { Phase = VideoSessionPhase.AwaitingReattachFrame; Arm(VideoFramePurpose.Reattach); }
    }
    public void BeginLoad(long generation)
    {
        Require(VideoSessionPhase.SurfaceReady);
        if (generation != Generation) throw InvalidState();
        contentRevision = checked(contentRevision + 1);
        Phase = VideoSessionPhase.Loading;
    }
    public void ContentLoaded()
    {
        Require(VideoSessionPhase.Loading);
        Phase = VideoSessionPhase.AwaitingInitialFrame;
        Arm(VideoFramePurpose.Initial);
    }
    public void BeginBounds(long commandGeneration, SetBoundsPayload bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        Require(VideoSessionPhase.Running);
        if (commandGeneration < Generation) throw InvalidState();
        ValidateBounds(bounds.Width, bounds.Height, bounds.ScaleFactor);
        width = bounds.Width; height = bounds.Height;
        // Host can send next-generation geometry before Attach. The acknowledgement
        // still belongs to the CURRENT binding/generation, as required by v1.
        Phase = VideoSessionPhase.AwaitingBoundsFrame;
        Arm(VideoFramePurpose.Bounds);
    }
    public VideoFrameRejection TryAcceptFrame(VideoFrameSubmission proof)
    {
        VideoFrameRejection result = VideoFirstFrameGate.Evaluate(PendingFrame, proof, lastAcceptedSequence);
        if (result != VideoFrameRejection.None) return result;
        lastAcceptedSequence = proof.FrameSequence;
        PendingFrame = null;
        Phase = VideoSessionPhase.Running;
        return VideoFrameRejection.None;
    }
    public void ValidatePlayback(long generation)
    {
        Require(VideoSessionPhase.Running);
        if (generation != Generation) throw InvalidState();
    }
    public bool BeginShutdown()
    {
        if (Phase is VideoSessionPhase.Stopping or VideoSessionPhase.Stopped) return false;
        PendingFrame = null;
        Phase = VideoSessionPhase.Stopping;
        return true;
    }
    public void ShutdownCompleted()
    {
        Require(VideoSessionPhase.Stopping);
        Phase = VideoSessionPhase.Stopped;
    }
    public void Fault()
    {
        PendingFrame = null;
        if (Phase != VideoSessionPhase.Stopped) Phase = VideoSessionPhase.Faulted;
    }
    private void Arm(VideoFramePurpose purpose) => PendingFrame = new(sessionId, Generation, bindingEpoch,
        contentRevision, checked(++operation), width, height, purpose);
    private static void ValidateBounds(int width, int height, double scale)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (width > 16384 || height > 16384 || (long)width * height > 67108864)
            throw new ArgumentOutOfRangeException(nameof(width), "Video target exceeds the bounded GPU allocation size.");
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
    }
    private void Require(VideoSessionPhase phase)
    {
        if (Phase != phase) throw InvalidState();
    }
    private InvalidOperationException InvalidState() => new($"Video transaction is {Phase} at generation {Generation}.");
}
