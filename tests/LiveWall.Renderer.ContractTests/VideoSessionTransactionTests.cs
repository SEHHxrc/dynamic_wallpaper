using FluentAssertions;
using LiveWall.Contracts.RendererProtocol;
using LiveWall.Renderer.Video.Protocol;

namespace LiveWall.Renderer.ContractTests;

public sealed class VideoSessionTransactionTests
{
    [Theory]
    [InlineData(null, 0, 0, 0, VideoFrameRejection.RenderNotConfirmed)]
    [InlineData(-1, 0, 0, 0, VideoFrameRejection.RenderNotConfirmed)]
    [InlineData(1, 0, 0, 0, VideoFrameRejection.RenderNotConfirmed)]
    [InlineData(0, null, 0, 0, VideoFrameRejection.PresentNotConfirmed)]
    [InlineData(0, -1, 0, 0, VideoFrameRejection.PresentNotConfirmed)]
    [InlineData(0, 0x087A0001, 0, 0, VideoFrameRejection.PresentNotConfirmed)]
    [InlineData(0, 0, null, 0, VideoFrameRejection.CommitNotConfirmed)]
    [InlineData(0, 0, 1, 0, VideoFrameRejection.CommitNotConfirmed)]
    [InlineData(0, 0, 0, null, VideoFrameRejection.FlushNotConfirmed)]
    [InlineData(0, 0, 0, -1, VideoFrameRejection.FlushNotConfirmed)]
    public void InitialFrameRequiresExactSuccessfulSubmission(int? render, int? present, int? commit, int? flush,
        object rejection)
    {
        VideoSessionTransaction state = AwaitingFrame();
        VideoFrameSubmission evidence = Proof(state) with { RenderResult = render, PresentResult = present, CommitResult = commit, FlushResult = flush };
        state.TryAcceptFrame(evidence).Should().Be((VideoFrameRejection)rejection);
        state.Phase.Should().Be(VideoSessionPhase.AwaitingInitialFrame);
        state.PendingFrame.Should().NotBeNull();
    }

    [Theory]
    [InlineData("session")]
    [InlineData("generation")]
    [InlineData("epoch")]
    [InlineData("content")]
    [InlineData("operation")]
    [InlineData("bounds")]
    [InlineData("purpose")]
    public void EvidenceMustMatchTheEntirePrivateTicket(string field)
    {
        VideoSessionTransaction state = AwaitingFrame();
        VideoFrameTicket ticket = state.PendingFrame!;
        VideoFrameTicket stale = field switch
        {
            "session" => ticket with { SessionId = "another-session" },
            "generation" => ticket with { Generation = 0 },
            "epoch" => ticket with { BindingEpoch = Guid.NewGuid() },
            "content" => ticket with { ContentRevision = 0 },
            "operation" => ticket with { Operation = 0 },
            "bounds" => ticket with { Width = 1 },
            _ => ticket with { Purpose = VideoFramePurpose.Bounds },
        };
        state.TryAcceptFrame(Proof(state) with { Ticket = stale }).Should().Be(VideoFrameRejection.WrongTicket);
    }

    [Fact]
    public void FileLoadedOrOffscreenPixelsCannotAcknowledgeAnUnboundTarget()
    {
        VideoSessionTransaction state = AwaitingFrame();
        state.TryAcceptFrame(Proof(state) with { TargetBoundToChild = false }).Should().Be(VideoFrameRejection.TargetNotBound);
        state.TryAcceptFrame(Proof(state) with { HasVideoFrame = false }).Should().Be(VideoFrameRejection.NoVideoFrame);
        state.TryAcceptFrame(Proof(state) with { Width = 319 }).Should().Be(VideoFrameRejection.WrongDimensions);
        state.TryAcceptFrame(Proof(state) with { FrameSequence = 0 }).Should().Be(VideoFrameRejection.SequenceInvalid);
        state.TryAcceptFrame(Proof(state) with { ReusesResidentTargetFrame = true }).Should().Be(VideoFrameRejection.ResidentFrameNotEligible);
    }

    [Fact]
    public void InitialLoadDoesNotBecomeRunningUntilSubmissionIsAcceptedExactlyOnce()
    {
        VideoSessionTransaction state = AwaitingFrame();
        VideoFrameSubmission proof = Proof(state);
        Action playback = () => state.ValidatePlayback(1);
        playback.Should().Throw<InvalidOperationException>();
        state.TryAcceptFrame(proof).Should().Be(VideoFrameRejection.None);
        state.ValidatePlayback(1);
        state.TryAcceptFrame(proof).Should().Be(VideoFrameRejection.NoPendingFrame);
        state.Phase.Should().Be(VideoSessionPhase.Running);
    }

    [Fact]
    public void ReattachRequiresNewGenerationBindingAndSubmissionNotAnotherLoad()
    {
        VideoSessionTransaction state = AwaitingFrame();
        VideoFrameSubmission first = Proof(state);
        state.TryAcceptFrame(first).Should().Be(VideoFrameRejection.None);
        Action duplicate = () => state.BeginAttach(1, Surface);
        duplicate.Should().Throw<InvalidOperationException>();
        state.BeginAttach(2, Surface); state.SurfaceAttached();
        state.Phase.Should().Be(VideoSessionPhase.AwaitingReattachFrame);
        state.PendingFrame!.ContentRevision.Should().Be(first.Ticket.ContentRevision);
        state.PendingFrame.BindingEpoch.Should().NotBe(first.Ticket.BindingEpoch);
        state.TryAcceptFrame(first).Should().Be(VideoFrameRejection.WrongTicket);
        state.TryAcceptFrame(Proof(state)).Should().Be(VideoFrameRejection.SequenceInvalid);
        state.TryAcceptFrame(Proof(state) with { FrameSequence = 2 }).Should().Be(VideoFrameRejection.None);
    }

    [Fact]
    public void BoundsAcknowledgementRetainsCurrentGenerationAndCanCommitResidentFrame()
    {
        VideoSessionTransaction state = AwaitingFrame();
        VideoFrameSubmission first = Proof(state);
        state.TryAcceptFrame(first).Should().Be(VideoFrameRejection.None);
        state.BeginBounds(2, new SetBoundsPayload(0, 0, 640, 360, 1));
        state.Generation.Should().Be(1);
        state.PendingFrame!.BindingEpoch.Should().Be(first.Ticket.BindingEpoch);
        state.PendingFrame.Operation.Should().BeGreaterThan(first.Ticket.Operation);
        state.TryAcceptFrame(first).Should().Be(VideoFrameRejection.WrongTicket);
        VideoFrameSubmission transformed = Proof(state) with { FrameSequence = 1, ReusesResidentTargetFrame = true,
            RenderResult = null, PresentResult = null, FlushResult = null };
        state.TryAcceptFrame(transformed with { CommitResult = 1 }).Should().Be(VideoFrameRejection.CommitNotConfirmed);
        state.TryAcceptFrame(transformed).Should().Be(VideoFrameRejection.None);
    }

    [Theory]
    [InlineData(VideoSessionPhase.Created)]
    [InlineData(VideoSessionPhase.Initializing)]
    [InlineData(VideoSessionPhase.Initialized)]
    [InlineData(VideoSessionPhase.Attaching)]
    [InlineData(VideoSessionPhase.SurfaceReady)]
    [InlineData(VideoSessionPhase.Loading)]
    [InlineData(VideoSessionPhase.AwaitingInitialFrame)]
    [InlineData(VideoSessionPhase.Running)]
    [InlineData(VideoSessionPhase.AwaitingBoundsFrame)]
    [InlineData(VideoSessionPhase.AwaitingReattachFrame)]
    public void ShutdownAtEveryStageInvalidatesLateEvidence(object phaseValue)
    {
        VideoSessionPhase phase = (VideoSessionPhase)phaseValue;
        VideoSessionTransaction state = AtPhase(phase);
        VideoFrameSubmission late = Proof(AwaitingFrame());
        state.BeginShutdown().Should().BeTrue();
        state.BeginShutdown().Should().BeFalse();
        state.TryAcceptFrame(late).Should().Be(VideoFrameRejection.NoPendingFrame);
        state.ShutdownCompleted();
        state.BeginShutdown().Should().BeFalse();
        state.TryAcceptFrame(late).Should().Be(VideoFrameRejection.NoPendingFrame);
        state.Fault(); state.Phase.Should().Be(VideoSessionPhase.Stopped);
    }

    [Fact]
    public void FaultDropsPendingFrameAndOutOfOrderCompletionsAreRejected()
    {
        VideoSessionTransaction state = new("video-session", 1);
        Action attach = () => state.BeginAttach(1, Surface);
        attach.Should().Throw<InvalidOperationException>();
        Action loaded = state.ContentLoaded;
        loaded.Should().Throw<InvalidOperationException>();
        state = AwaitingFrame();
        VideoFrameSubmission late = Proof(state);
        state.Fault(); state.TryAcceptFrame(late).Should().Be(VideoFrameRejection.NoPendingFrame);
        state.Phase.Should().Be(VideoSessionPhase.Faulted);
        state.BeginShutdown().Should().BeTrue(); state.ShutdownCompleted();
    }

    private static AttachSurfacePayload Surface => new(1, 320, 180, 1);
    private static VideoSessionTransaction AwaitingFrame() => AtPhase(VideoSessionPhase.AwaitingInitialFrame);
    private static VideoFrameSubmission Proof(VideoSessionTransaction state) => new(state.PendingFrame!, 1,
        state.PendingFrame!.Width, state.PendingFrame.Height, true, true, 0, 0, 0, 0);
    private static VideoSessionTransaction AtPhase(VideoSessionPhase phase)
    {
        VideoSessionTransaction state = new("video-session", 1);
        if (phase == state.Phase) return state;
        state.BeginInitialize(); if (phase == state.Phase) return state;
        state.Initialized(); if (phase == state.Phase) return state;
        state.BeginAttach(1, Surface); if (phase == state.Phase) return state;
        state.SurfaceAttached(); if (phase == state.Phase) return state;
        state.BeginLoad(1); if (phase == state.Phase) return state;
        state.ContentLoaded(); if (phase == state.Phase) return state;
        state.TryAcceptFrame(Proof(state)); if (phase == state.Phase) return state;
        if (phase == VideoSessionPhase.AwaitingBoundsFrame) state.BeginBounds(2, new(0, 0, 640, 360, 1));
        else { state.BeginAttach(2, Surface); state.SurfaceAttached(); }
        return state;
    }
}
