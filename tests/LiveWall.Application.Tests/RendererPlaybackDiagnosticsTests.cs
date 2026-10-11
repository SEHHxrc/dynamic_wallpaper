using FluentAssertions;
using LiveWall.Domain.Playback;
using LiveWall.Host.Orchestration;

namespace LiveWall.Application.Tests;

public sealed class RendererPlaybackDiagnosticsTests
{
    [Fact]
    public void PolicyAndWriteWithoutFeedbackCannotProvePause()
    {
        RendererPlaybackJournal journal = new();
        journal.Record("session", 1, RendererPlaybackEvidenceKind.PolicyDecided, "Pause", PlaybackState.Paused);
        journal.Record("session", 1, RendererPlaybackEvidenceKind.WriteStarted, "Pause", PlaybackState.Paused);
        journal.Record("session", 1, RendererPlaybackEvidenceKind.WriteCompleted, "Pause", PlaybackState.Paused);
        WindowsHostEventChainDiagnostics.HasCompletedPlaybackExchange(journal.Snapshot(), PlaybackState.Paused)
            .Should().BeFalse();
    }

    [Fact]
    public void FastWireFeedbackBeforeWriteContinuationIsValid()
    {
        RendererPlaybackJournal journal = new();
        journal.Record("session", 1, RendererPlaybackEvidenceKind.WriteStarted, "Suspend", PlaybackState.Suspended);
        journal.Record("session", 1, RendererPlaybackEvidenceKind.FeedbackReceived, "PlaybackStateChanged", PlaybackState.Suspended);
        journal.Record("session", 1, RendererPlaybackEvidenceKind.WriteCompleted, "Suspend", PlaybackState.Suspended);
        WindowsHostEventChainDiagnostics.HasCompletedPlaybackExchange(journal.Snapshot(), PlaybackState.Suspended)
            .Should().BeTrue();
    }

    [Fact]
    public void AnotherGenerationFeedbackDoesNotProveCurrentExchange()
    {
        RendererPlaybackJournal journal = new();
        journal.Record("session", 2, RendererPlaybackEvidenceKind.WriteStarted, "Play", PlaybackState.Playing);
        journal.Record("session", 2, RendererPlaybackEvidenceKind.WriteCompleted, "Play", PlaybackState.Playing);
        journal.Record("session", 1, RendererPlaybackEvidenceKind.FeedbackReceived, "PlaybackStateChanged", PlaybackState.Playing);
        WindowsHostEventChainDiagnostics.HasCompletedPlaybackExchange(journal.Snapshot(), PlaybackState.Playing)
            .Should().BeFalse();
    }

    [Fact]
    public void JournalIsBoundedAndRetainsMonotonicSequence()
    {
        RendererPlaybackJournal journal = new();
        for (int index = 0; index < 300; index++)
        {
            journal.Record("session", 1, RendererPlaybackEvidenceKind.PolicyDecided, "Play", PlaybackState.Playing);
        }
        journal.Snapshot().Should().HaveCount(256);
        journal.Snapshot()[0].Sequence.Should().Be(45);
        journal.Snapshot()[^1].Sequence.Should().Be(300);
    }
}
