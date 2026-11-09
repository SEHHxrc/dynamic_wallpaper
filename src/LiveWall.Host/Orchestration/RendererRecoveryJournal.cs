using LiveWall.Domain.Displays;
using LiveWall.Domain.Wallpapers;

namespace LiveWall.Host.Orchestration;

public enum RendererRecoveryOutcome
{
    /// <summary>A Renderer failure was counted and a restart was scheduled after <see cref="RendererRecoveryEntry.Delay"/>.</summary>
    RestartScheduled,

    /// <summary>A restart Apply generation started with a fresh Renderer and Surface.</summary>
    RestartStarted,

    /// <summary>The restart generation committed its first frame.</summary>
    Restarted,

    /// <summary>The restart generation failed; the failure is counted like a crash.</summary>
    RestartFailed,

    /// <summary>Too many failures inside the window: no more automatic restarts until an explicit Apply.</summary>
    CircuitBroken,

    /// <summary>Nothing to restart any more, for the reason in <see cref="RendererRecoveryEntry.Detail"/>.</summary>
    Superseded,

    /// <summary>The Host stayed busy for too long; the restart was given up.</summary>
    Abandoned,
}

public sealed record RendererRecoveryEntry(
    long Sequence,
    DateTimeOffset RecordedAt,
    WallpaperId WallpaperId,
    IReadOnlyList<DisplayId> Displays,
    RendererRecoveryOutcome Outcome,
    long? Generation,
    int RecentFailures,
    TimeSpan Delay,
    string? Detail);
