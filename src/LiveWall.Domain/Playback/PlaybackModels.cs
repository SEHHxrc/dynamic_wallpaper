namespace LiveWall.Domain.Playback;

public enum PlaybackState
{
    Starting,
    Playing,
    Throttled,
    Paused,
    Suspended,
    Stopped,
    Faulted,
}

public sealed record SystemStateSnapshot(
    bool IsDisplayOff,
    bool IsSessionLocked,
    bool IsSessionDisconnected,
    bool IsRemoteSession,
    bool IsOnBattery,
    bool HasFullscreenApplication);

public sealed record UserPlaybackPolicy
{
    public UserPlaybackPolicy(
        bool pauseWhenDisplayOff,
        bool pauseWhenSessionLocked,
        bool pauseInRemoteSession,
        bool pauseOnBattery,
        bool pauseForFullscreenApplication,
        int defaultFramesPerSecond,
        int throttledFramesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(defaultFramesPerSecond, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(defaultFramesPerSecond, 240);
        ArgumentOutOfRangeException.ThrowIfLessThan(throttledFramesPerSecond, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(throttledFramesPerSecond, defaultFramesPerSecond);

        PauseWhenDisplayOff = pauseWhenDisplayOff;
        PauseWhenSessionLocked = pauseWhenSessionLocked;
        PauseInRemoteSession = pauseInRemoteSession;
        PauseOnBattery = pauseOnBattery;
        PauseForFullscreenApplication = pauseForFullscreenApplication;
        DefaultFramesPerSecond = defaultFramesPerSecond;
        ThrottledFramesPerSecond = throttledFramesPerSecond;
    }

    public static UserPlaybackPolicy Default { get; } =
        new(true, true, true, false, true, 30, 15);

    public bool PauseWhenDisplayOff { get; init; }

    public bool PauseWhenSessionLocked { get; init; }

    public bool PauseInRemoteSession { get; init; }

    public bool PauseOnBattery { get; init; }

    public bool PauseForFullscreenApplication { get; init; }

    public int DefaultFramesPerSecond { get; init; }

    public int ThrottledFramesPerSecond { get; init; }
}

public sealed record WallpaperSessionSnapshot(
    UserPlaybackIntent UserIntent,
    PlaybackState State,
    int CurrentFramesPerSecond,
    bool IsMuted);

public enum UserPlaybackIntent
{
    Play,
    Pause,
    Stop,
}

public sealed record PlaybackDecision(
    PlaybackState TargetState,
    PlaybackDecisionReason Reason,
    int FramesPerSecond,
    bool Muted,
    PlaybackQuality Quality);

public enum PlaybackDecisionReason
{
    UserRequest,
    DisplayOff,
    SessionLocked,
    SessionDisconnected,
    RemoteSession,
    BatteryPolicy,
    FullscreenApplication,
    NormalOperation,
}

public enum PlaybackQuality
{
    Low,
    Balanced,
    High,
}
