using FluentAssertions;
using LiveWall.Application.Policies;
using LiveWall.Domain.Playback;

namespace LiveWall.Application.Tests;

public sealed class PlaybackPolicyEvaluatorTests
{
    private readonly DefaultPlaybackPolicyEvaluator evaluator = new();

    [Fact]
    public void UserStopWinsOverSystemStateAndNeverAutoResumes()
    {
        SystemStateSnapshot system = new(true, true, true, true, true, true);
        WallpaperSessionSnapshot session = new(UserPlaybackIntent.Stop, PlaybackState.Stopped, 0, false);

        PlaybackDecision decision = evaluator.Evaluate(system, CreatePolicy(), session);

        decision.TargetState.Should().Be(PlaybackState.Stopped);
        decision.Reason.Should().Be(PlaybackDecisionReason.UserRequest);
        decision.FramesPerSecond.Should().Be(0);
        decision.Muted.Should().BeTrue();
    }

    [Fact]
    public void LockedSessionIsSuspendedBeforeFullscreenPolicyIsConsidered()
    {
        SystemStateSnapshot system = new(false, true, false, false, false, true);
        WallpaperSessionSnapshot session = new(UserPlaybackIntent.Play, PlaybackState.Playing, 30, false);

        PlaybackDecision decision = evaluator.Evaluate(system, CreatePolicy(), session);

        decision.TargetState.Should().Be(PlaybackState.Suspended);
        decision.Reason.Should().Be(PlaybackDecisionReason.SessionLocked);
    }

    [Fact]
    public void BatteryModeThrottlesWhenPauseOnBatteryIsDisabled()
    {
        SystemStateSnapshot system = new(false, false, false, false, true, false);
        UserPlaybackPolicy policy = CreatePolicy() with { PauseOnBattery = false };
        WallpaperSessionSnapshot session = new(UserPlaybackIntent.Play, PlaybackState.Playing, 30, false);

        PlaybackDecision decision = evaluator.Evaluate(system, policy, session);

        decision.TargetState.Should().Be(PlaybackState.Throttled);
        decision.FramesPerSecond.Should().Be(15);
        decision.Quality.Should().Be(PlaybackQuality.Low);
    }

    [Fact]
    public void NormalOperationPreservesTheSessionMutePreference()
    {
        SystemStateSnapshot system = new(false, false, false, false, false, false);
        WallpaperSessionSnapshot session = new(UserPlaybackIntent.Play, PlaybackState.Paused, 0, true);

        PlaybackDecision decision = evaluator.Evaluate(system, CreatePolicy(), session);

        decision.TargetState.Should().Be(PlaybackState.Playing);
        decision.FramesPerSecond.Should().Be(30);
        decision.Muted.Should().BeTrue();
    }

    private static UserPlaybackPolicy CreatePolicy() =>
        new(
            pauseWhenDisplayOff: true,
            pauseWhenSessionLocked: true,
            pauseInRemoteSession: true,
            pauseOnBattery: true,
            pauseForFullscreenApplication: true,
            defaultFramesPerSecond: 30,
            throttledFramesPerSecond: 15);
}
