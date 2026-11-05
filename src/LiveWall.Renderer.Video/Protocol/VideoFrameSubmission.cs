namespace LiveWall.Renderer.Video.Protocol;

internal enum VideoFramePurpose { Initial, Reattach, Bounds }
internal enum VideoFrameRejection
{
    None, NoPendingFrame, WrongTicket, TargetNotBound, WrongDimensions, NoVideoFrame,
    SequenceInvalid, RenderNotConfirmed, PresentNotConfirmed, CommitNotConfirmed,
    FlushNotConfirmed, ResidentFrameNotEligible,
}

// Process-private operation identity. Never serialized into Renderer Protocol v1.
internal sealed record VideoFrameTicket(string SessionId, long Generation, Guid BindingEpoch,
    long ContentRevision, long Operation, int Width, int Height, VideoFramePurpose Purpose);

// Values must come from the Renderer-owned submission path, not from file/VO events or IPC.
// HRESULT 0 is required: a non-negative DXGI status is not successful presentation evidence.
// FrameSequence counts successful target submissions, NOT decoded frames/media time.
// A paused current frame freshly rendered/presented to a new binding gets a new sequence.
internal sealed record VideoFrameSubmission(VideoFrameTicket Ticket, long FrameSequence,
    int Width, int Height, bool HasVideoFrame, bool TargetBoundToChild,
    int? RenderResult, int? PresentResult, int? CommitResult, int? FlushResult,
    bool ReusesResidentTargetFrame = false);

internal static class VideoFirstFrameGate
{
    public static VideoFrameRejection Evaluate(VideoFrameTicket? expected, VideoFrameSubmission proof,
        long lastAcceptedSequence)
    {
        ArgumentNullException.ThrowIfNull(proof);
        if (expected is null) return VideoFrameRejection.NoPendingFrame;
        if (expected != proof.Ticket) return VideoFrameRejection.WrongTicket;
        if (!proof.TargetBoundToChild) return VideoFrameRejection.TargetNotBound;
        if (proof.Width != expected.Width || proof.Height != expected.Height) return VideoFrameRejection.WrongDimensions;
        if (!proof.HasVideoFrame) return VideoFrameRejection.NoVideoFrame;
        if (proof.ReusesResidentTargetFrame)
        {
            // Existing SetBounds contract permits transforming the already-presented frame
            // in the SAME binding. A fresh generation/target must render and present again.
            if (expected.Purpose != VideoFramePurpose.Bounds || lastAcceptedSequence <= 0 ||
                proof.FrameSequence != lastAcceptedSequence) return VideoFrameRejection.ResidentFrameNotEligible;
            return proof.CommitResult == 0 ? VideoFrameRejection.None : VideoFrameRejection.CommitNotConfirmed;
        }
        if (proof.FrameSequence <= lastAcceptedSequence || proof.FrameSequence <= 0) return VideoFrameRejection.SequenceInvalid;
        if (proof.RenderResult != 0) return VideoFrameRejection.RenderNotConfirmed;
        if (proof.PresentResult != 0) return VideoFrameRejection.PresentNotConfirmed;
        if (proof.CommitResult != 0) return VideoFrameRejection.CommitNotConfirmed;
        if (expected.Purpose != VideoFramePurpose.Bounds && proof.FlushResult != 0) return VideoFrameRejection.FlushNotConfirmed;
        return VideoFrameRejection.None;
    }
}
