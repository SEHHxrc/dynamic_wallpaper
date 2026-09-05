using LiveWall.Domain.Playback;

namespace LiveWall.Application.Policies;

public interface IPlaybackPolicyEvaluator
{
    PlaybackDecision Evaluate(
        SystemStateSnapshot system,
        UserPlaybackPolicy policy,
        WallpaperSessionSnapshot session);
}

