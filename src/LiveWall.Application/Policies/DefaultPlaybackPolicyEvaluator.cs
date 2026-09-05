using LiveWall.Domain.Playback;

namespace LiveWall.Application.Policies;

public sealed class DefaultPlaybackPolicyEvaluator : IPlaybackPolicyEvaluator
{
    public PlaybackDecision Evaluate(
        SystemStateSnapshot system,
        UserPlaybackPolicy policy,
        WallpaperSessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(session);

        ValidateFramesPerSecond(policy.DefaultFramesPerSecond, nameof(policy.DefaultFramesPerSecond));
        ValidateFramesPerSecond(policy.ThrottledFramesPerSecond, nameof(policy.ThrottledFramesPerSecond));

        if (session.UserIntent == UserPlaybackIntent.Stop)
        {
            return Inactive(PlaybackState.Stopped, PlaybackDecisionReason.UserRequest);
        }

        if (system.IsDisplayOff && policy.PauseWhenDisplayOff)
        {
            return Inactive(PlaybackState.Suspended, PlaybackDecisionReason.DisplayOff);
        }

        if (system.IsSessionLocked && policy.PauseWhenSessionLocked)
        {
            return Inactive(PlaybackState.Suspended, PlaybackDecisionReason.SessionLocked);
        }

        if (system.IsSessionDisconnected)
        {
            return Inactive(PlaybackState.Suspended, PlaybackDecisionReason.SessionDisconnected);
        }

        if (system.IsRemoteSession && policy.PauseInRemoteSession)
        {
            return Inactive(PlaybackState.Suspended, PlaybackDecisionReason.RemoteSession);
        }

        if (session.UserIntent == UserPlaybackIntent.Pause)
        {
            return Inactive(PlaybackState.Paused, PlaybackDecisionReason.UserRequest);
        }

        if (system.HasFullscreenApplication && policy.PauseForFullscreenApplication)
        {
            return Inactive(PlaybackState.Paused, PlaybackDecisionReason.FullscreenApplication);
        }

        if (system.IsOnBattery)
        {
            if (policy.PauseOnBattery)
            {
                return Inactive(PlaybackState.Paused, PlaybackDecisionReason.BatteryPolicy);
            }

            return new PlaybackDecision(
                PlaybackState.Throttled,
                PlaybackDecisionReason.BatteryPolicy,
                policy.ThrottledFramesPerSecond,
                session.IsMuted,
                PlaybackQuality.Low);
        }

        return new PlaybackDecision(
            PlaybackState.Playing,
            PlaybackDecisionReason.NormalOperation,
            policy.DefaultFramesPerSecond,
            session.IsMuted,
            PlaybackQuality.High);
    }

    private static PlaybackDecision Inactive(
        PlaybackState state,
        PlaybackDecisionReason reason) =>
        new(state, reason, 0, true, PlaybackQuality.Low);

    private static void ValidateFramesPerSecond(int value, string parameterName)
    {
        if (value is < 1 or > 240)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "FPS must be between 1 and 240.");
        }
    }
}
