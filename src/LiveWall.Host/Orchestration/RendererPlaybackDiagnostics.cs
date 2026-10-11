using LiveWall.Domain.Playback;

namespace LiveWall.Host.Orchestration;

public enum RendererPlaybackEvidenceKind
{
    PolicyDecided,
    WriteStarted,
    WriteCompleted,
    WriteFailed,
    FeedbackReceived,
}

// Local evidence only: no credentials, pipe names, Shell handles, or protocol changes.
public sealed record RendererPlaybackEvidence(
    long Sequence,
    TimeSpan Elapsed,
    string SessionId,
    long Generation,
    RendererPlaybackEvidenceKind Kind,
    string Command,
    PlaybackState State,
    PlaybackDecisionReason? PolicyReason = null,
    string? FailureType = null);

internal sealed class RendererPlaybackJournal
{
    private readonly Lock gate = new();
    private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly Queue<RendererPlaybackEvidence> entries = new();
    private long sequence;

    public void Record(string sessionId, long generation, RendererPlaybackEvidenceKind kind,
        string command, PlaybackState state, PlaybackDecisionReason? reason = null,
        string? failureType = null)
    {
        lock (gate)
        {
            entries.Enqueue(new(++sequence, clock.Elapsed, sessionId, generation, kind,
                command, state, reason, failureType));
            while (entries.Count > 256)
            {
                entries.Dequeue();
            }
        }
    }

    public IReadOnlyList<RendererPlaybackEvidence> Snapshot()
    {
        lock (gate)
        {
            return entries.ToArray();
        }
    }
}
