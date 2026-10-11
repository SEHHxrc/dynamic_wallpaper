using System.Runtime.InteropServices;
using LiveWall.Application.Policies;
using LiveWall.Domain.Playback;
using LiveWall.Platform.Windows.Desktop;
using LiveWall.Platform.Windows.NativeMethods;

namespace LiveWall.Platform.Windows.Power;

internal interface IWindowsSystemStateSource : ISystemStateSource
{
    void ProcessSignal(DesktopWindowSignal signal);

    void CompleteEnvironmentValidation(bool shellAvailable, long? expectedRevision = null);

    bool RejectEnvironmentValidation(long expectedRevision);
}

public sealed class WindowsSystemStateSource : IWindowsSystemStateSource
{
    private readonly object gate = new();
    private SystemStateChange current;
    private PendingEnvironmentResume? pendingResume;

    public WindowsSystemStateSource()
        : this(CaptureInitialSnapshot())
    {
    }

    internal WindowsSystemStateSource(SystemStateSnapshot initialSnapshot)
    {
        current = new SystemStateChange(
            1,
            initialSnapshot ?? throw new ArgumentNullException(nameof(initialSnapshot)),
            SystemStateChangeReason.Initial);
    }

    public SystemStateChange Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    public event EventHandler<SystemStateChangedEventArgs>? Changed;

    void IWindowsSystemStateSource.ProcessSignal(DesktopWindowSignal signal) =>
        ProcessSignal(signal);

    void IWindowsSystemStateSource.CompleteEnvironmentValidation(bool shellAvailable, long? expectedRevision) =>
        CompleteEnvironmentValidation(shellAvailable, expectedRevision);

    bool IWindowsSystemStateSource.RejectEnvironmentValidation(long expectedRevision) =>
        RejectEnvironmentValidation(expectedRevision);

    internal bool RejectEnvironmentValidation(long expectedRevision)
    {
        lock (gate)
        {
            if (current.Revision != expectedRevision || pendingResume is null)
            {
                return false;
            }
            pendingResume = null;
            return true;
        }
    }

    internal void ProcessSignal(DesktopWindowSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.SessionId is int sessionId &&
            sessionId != System.Diagnostics.Process.GetCurrentProcess().SessionId)
        {
            return;
        }
        SystemStateChange? change = signal.Kind switch
        {
            DesktopWindowSignalKind.SessionLocked => PublishImmediate(
                snapshot => snapshot with { IsSessionLocked = true },
                SystemStateChangeReason.SessionLocked),
            DesktopWindowSignalKind.SessionUnlocked => BeginValidatedResume(
                snapshot => snapshot with { IsSessionLocked = false },
                SystemStateChangeReason.SessionUnlockRequested,
                SystemStateChangeReason.SessionUnlocked),
            DesktopWindowSignalKind.DisplayPoweredOff => PublishImmediate(
                snapshot => snapshot with { IsDisplayOff = true },
                SystemStateChangeReason.DisplayOff),
            DesktopWindowSignalKind.DisplayPoweredOn => BeginValidatedResume(
                snapshot => snapshot with { IsDisplayOff = false },
                SystemStateChangeReason.DisplayOnRequested,
                SystemStateChangeReason.DisplayOn),
            DesktopWindowSignalKind.SystemSuspending => PublishImmediate(
                snapshot => snapshot with { IsSystemSuspended = true },
                SystemStateChangeReason.SystemSuspending),
            DesktopWindowSignalKind.SystemResuming => BeginValidatedResume(
                snapshot => snapshot with { IsSystemSuspended = false },
                SystemStateChangeReason.SystemResumeRequested,
                SystemStateChangeReason.SystemResumed),
            DesktopWindowSignalKind.SessionDisconnected => PublishImmediate(
                snapshot => snapshot with { IsSessionDisconnected = true },
                SystemStateChangeReason.SessionDisconnected),
            DesktopWindowSignalKind.SessionReconnected => BeginValidatedResume(
                snapshot => snapshot with
                {
                    IsSessionDisconnected = false,
                    IsRemoteSession = signal.Value != 0,
                },
                SystemStateChangeReason.SessionReconnectRequested,
                SystemStateChangeReason.SessionReconnected),
            DesktopWindowSignalKind.RemoteSessionChanged => PublishImmediate(
                snapshot => snapshot with { IsRemoteSession = signal.Value != 0 },
                SystemStateChangeReason.RemoteSessionChanged),
            DesktopWindowSignalKind.PowerSourceChanged => PublishImmediate(
                snapshot => snapshot with { IsOnBattery = signal.Value != 0 },
                SystemStateChangeReason.PowerSourceChanged),
            _ => null,
        };
        RaiseChanged(change);
    }

    internal void CompleteEnvironmentValidation(bool shellAvailable, long? expectedRevision = null)
    {
        List<SystemStateChange> changes = [];
        lock (gate)
        {
            if (!shellAvailable || pendingResume is null ||
                (expectedRevision is long revision && revision != current.Revision))
            {
                return;
            }

            PendingEnvironmentResume pending = pendingResume;
            pendingResume = null;
            foreach (SystemStateChangeReason reason in pending.CompletedReasons)
            {
                current = new SystemStateChange(
                    checked(current.Revision + 1),
                    pending.TargetSnapshot,
                    reason);
                changes.Add(current);
            }
        }
        foreach (SystemStateChange change in changes)
        {
            RaiseChanged(change);
        }
    }

    private SystemStateChange? PublishImmediate(
        Func<SystemStateSnapshot, SystemStateSnapshot> update,
        SystemStateChangeReason reason)
    {
        lock (gate)
        {
            if (pendingResume is not null)
            {
                // An inactive fact supersedes only its own pending resume, not
                // unrelated unlock/display/power/session facts awaiting validation.
                SystemStateChangeReason? superseded = reason switch
                {
                    SystemStateChangeReason.SessionLocked => SystemStateChangeReason.SessionUnlocked,
                    SystemStateChangeReason.DisplayOff => SystemStateChangeReason.DisplayOn,
                    SystemStateChangeReason.SystemSuspending => SystemStateChangeReason.SystemResumed,
                    SystemStateChangeReason.SessionDisconnected => SystemStateChangeReason.SessionReconnected,
                    _ => null,
                };
                pendingResume = pendingResume with
                {
                    TargetSnapshot = update(pendingResume.TargetSnapshot),
                    CompletedReasons = pendingResume.CompletedReasons
                        .Where(item => item != superseded).ToArray(),
                };
                if (pendingResume.CompletedReasons.Length == 0)
                {
                    pendingResume = null;
                }
            }
            SystemStateSnapshot next = update(current.Snapshot);
            if (next == current.Snapshot &&
                current.RequiresEnvironmentValidation == (pendingResume is not null))
            {
                return null;
            }

            current = new SystemStateChange(checked(current.Revision + 1), next, reason,
                RequiresEnvironmentValidation: pendingResume is not null);
            return current;
        }
    }

    private SystemStateChange? BeginValidatedResume(
        Func<SystemStateSnapshot, SystemStateSnapshot> update,
        SystemStateChangeReason requestedReason,
        SystemStateChangeReason completedReason)
    {
        lock (gate)
        {
            SystemStateSnapshot target = update(pendingResume?.TargetSnapshot ?? current.Snapshot);
            if (pendingResume is null && target == current.Snapshot)
            {
                return null;
            }
            List<SystemStateChangeReason> completedReasons = pendingResume?.CompletedReasons
                .ToList() ?? [];
            if (pendingResume is not null && pendingResume.TargetSnapshot == target &&
                completedReasons.Contains(completedReason))
            {
                return null;
            }
            if (!completedReasons.Contains(completedReason))
            {
                completedReasons.Add(completedReason);
            }
            pendingResume = new PendingEnvironmentResume(target, completedReasons.ToArray());
            current = new SystemStateChange(
                checked(current.Revision + 1),
                requestedReason == SystemStateChangeReason.SessionReconnectRequested
                    ? current.Snapshot with { IsSessionDisconnected = true }
                    : current.Snapshot,
                requestedReason,
                RequiresEnvironmentValidation: true);
            return current;
        }
    }

    private void RaiseChanged(SystemStateChange? change)
    {
        if (change is not null)
        {
            Changed?.Invoke(this, new SystemStateChangedEventArgs(change));
        }
    }

    private static SystemStateSnapshot CaptureInitialSnapshot()
    {
        bool disconnected = false;
        if (SystemStateNativeMethods.WTSQuerySessionInformation(
                0,
                SystemStateNativeMethods.WtsCurrentSession,
                SystemStateNativeMethods.WtsConnectState,
                out nint buffer,
                out uint bytesReturned))
        {
            try
            {
                int state = bytesReturned >= sizeof(int) ? Marshal.ReadInt32(buffer) : 0;
                disconnected = state is 4 or 7 or 8;
            }
            finally
            {
                SystemStateNativeMethods.WTSFreeMemory(buffer);
            }
        }

        bool onBattery = SystemStateNativeMethods.GetSystemPowerStatus(out NativeSystemPowerStatus power) &&
            power.AcLineStatus == 0;
        bool remote = SystemStateNativeMethods.GetSystemMetrics(
            SystemStateNativeMethods.SystemMetricRemoteSession) != 0;
        return new SystemStateSnapshot(
            IsDisplayOff: false,
            IsSessionLocked: !SystemStateNativeMethods.IsDefaultInputDesktop(),
            IsSessionDisconnected: disconnected,
            IsRemoteSession: remote,
            IsOnBattery: onBattery,
            HasFullscreenApplication: false,
            IsSystemSuspended: false);
    }

    private sealed record PendingEnvironmentResume(
        SystemStateSnapshot TargetSnapshot,
        SystemStateChangeReason[] CompletedReasons);
}
