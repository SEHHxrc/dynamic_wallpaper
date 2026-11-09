using System.Diagnostics.CodeAnalysis;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Sessions;
using LiveWall.Domain.Wallpapers;
using LiveWall.Host.Orchestration;

namespace LiveWall.Host.CommandLoop;

/// <summary>The wallpaper and displays a failed session covered; failures and restarts are counted per key.</summary>
internal sealed record RendererRecoveryKey(WallpaperId WallpaperId, IReadOnlyList<DisplayId> Displays)
{
    public static RendererRecoveryKey For(WallpaperSession session) => new(
        session.WallpaperId,
        Array.AsReadOnly(session.Displays.OrderBy(display => display.Value, StringComparer.Ordinal).ToArray()));

    public bool Touches(IEnumerable<DisplayId> displays) => displays.Any(Displays.Contains);

    public bool Equals(RendererRecoveryKey? other) =>
        other is not null && WallpaperId == other.WallpaperId && Displays.SequenceEqual(other.Displays);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(WallpaperId);
        foreach (DisplayId display in Displays)
        {
            hash.Add(display);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// Bookkeeping for ARCHITECTURE §11.5. Only the Host command loop calls the mutating members, so they need
/// no locking; the journal is also read by diagnostics and is guarded.
/// </summary>
internal sealed class RendererRecoveryTracker(TimeProvider timeProvider)
{
    private const int RetainedEntries = 64;
    private readonly Dictionary<RendererRecoveryKey, List<DateTimeOffset>> failures = [];
    private readonly HashSet<RendererRecoveryKey> openCircuits = [];
    private readonly Dictionary<long, RendererRecoveryKey> restartGenerations = [];
    private readonly Lock journalGate = new();
    private readonly List<RendererRecoveryEntry> journal = [];
    private long sequence;

    public IReadOnlyList<RendererRecoveryEntry> Journal
    {
        get
        {
            lock (journalGate)
            {
                return Array.AsReadOnly(journal.ToArray());
            }
        }
    }

    /// <summary>Counts one failure inside the 60-second window and decides the next step.</summary>
    public RendererRestartDecision RecordFailure(RendererRecoveryKey key, long? generation, string reason)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (!failures.TryGetValue(key, out List<DateTimeOffset>? history))
        {
            history = [];
            failures.Add(key, history);
        }

        history.RemoveAll(failure => now - failure > RendererRestartPolicy.FailureWindow);
        history.Add(now);
        RendererRestartDecision decision = RendererRestartPolicy.Evaluate(history, now);
        if (decision.CircuitBroken)
        {
            openCircuits.Add(key);
        }

        Record(key, decision.CircuitBroken ? RendererRecoveryOutcome.CircuitBroken : RendererRecoveryOutcome.RestartScheduled,
            generation, reason, history.Count, decision.Delay);
        return decision;
    }

    public bool IsCircuitOpen(RendererRecoveryKey key) => openCircuits.Contains(key);

    /// <summary>An explicit Apply or restore starts over for its displays, as for desktop recovery.</summary>
    public void Reset(IReadOnlyCollection<DisplayId> displays)
    {
        foreach (RendererRecoveryKey key in failures.Keys.Concat(openCircuits).Where(key => key.Touches(displays)).ToArray())
        {
            failures.Remove(key);
            openCircuits.Remove(key);
        }
    }

    public void BeginRestart(RendererRecoveryKey key, long generation)
    {
        restartGenerations[generation] = key;
        Record(key, RendererRecoveryOutcome.RestartStarted, generation, null);
    }

    public bool TryCompleteRestart(long generation, [NotNullWhen(true)] out RendererRecoveryKey? key) =>
        restartGenerations.Remove(generation, out key);

    public void Record(
        RendererRecoveryKey key,
        RendererRecoveryOutcome outcome,
        long? generation,
        string? detail,
        int recentFailures = 0,
        TimeSpan delay = default)
    {
        lock (journalGate)
        {
            journal.Add(new RendererRecoveryEntry(++sequence, timeProvider.GetUtcNow(), key.WallpaperId,
                key.Displays, outcome, generation, recentFailures, delay, detail));
            if (journal.Count > RetainedEntries)
            {
                journal.RemoveAt(0);
            }
        }
    }
}
