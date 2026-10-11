using LiveWall.Domain.Playback;

namespace LiveWall.Application.Policies;

public interface ISystemStateSource
{
    SystemStateChange Current { get; }

    event EventHandler<SystemStateChangedEventArgs>? Changed;
}

public sealed record SystemStateChange(
    long Revision,
    SystemStateSnapshot Snapshot,
    SystemStateChangeReason Reason,
    bool RequiresEnvironmentValidation = false);

public enum SystemStateChangeReason
{
    Initial,
    SessionLocked,
    SessionUnlockRequested,
    SessionUnlocked,
    DisplayOff,
    DisplayOnRequested,
    DisplayOn,
    SystemSuspending,
    SystemResumeRequested,
    SystemResumed,
    SessionDisconnected,
    SessionReconnectRequested,
    SessionReconnected,
    RemoteSessionChanged,
    PowerSourceChanged,
}

public sealed class SystemStateChangedEventArgs(SystemStateChange current) : EventArgs
{
    public SystemStateChange Current { get; } = current ??
        throw new ArgumentNullException(nameof(current));
}
