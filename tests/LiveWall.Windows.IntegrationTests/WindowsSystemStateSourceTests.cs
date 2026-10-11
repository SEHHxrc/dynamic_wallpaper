using FluentAssertions;
using LiveWall.Application.Policies;
using LiveWall.Domain.Playback;
using LiveWall.Platform.Windows.Desktop;
using LiveWall.Platform.Windows.Power;

namespace LiveWall.Windows.IntegrationTests;

public sealed class WindowsSystemStateSourceTests
{
    [Fact]
    public void RemoteReconnectWithoutPriorDisconnectStillRequiresPresentationValidation()
    {
        WindowsSystemStateSource source = CreateSource();
        source.ProcessSignal(new(DesktopWindowSignalKind.SessionReconnected, Value: 1));
        source.Current.Snapshot.IsSessionDisconnected.Should().BeTrue();
        source.Current.Snapshot.IsRemoteSession.Should().BeFalse();
        source.CompleteEnvironmentValidation(true, source.Current.Revision);
        source.Current.Snapshot.IsSessionDisconnected.Should().BeFalse();
        source.Current.Snapshot.IsRemoteSession.Should().BeTrue();
    }

    [Fact]
    public void DisconnectReconnectDuplicatesDoNotAdvanceRevisionAndValidationIsRequired()
    {
        WindowsSystemStateSource source = CreateSource();
        source.ProcessSignal(new(DesktopWindowSignalKind.SessionDisconnected));
        long disconnected = source.Current.Revision;
        source.ProcessSignal(new(DesktopWindowSignalKind.SessionDisconnected));
        source.Current.Revision.Should().Be(disconnected);
        source.ProcessSignal(new(DesktopWindowSignalKind.SessionReconnected));
        long reconnect = source.Current.Revision;
        source.ProcessSignal(new(DesktopWindowSignalKind.SessionReconnected));
        source.Current.Revision.Should().Be(reconnect);
        source.Current.Snapshot.IsSessionDisconnected.Should().BeTrue();
        source.CompleteEnvironmentValidation(false, reconnect);
        source.Current.Snapshot.IsSessionDisconnected.Should().BeTrue();
        source.CompleteEnvironmentValidation(true, reconnect);
        source.Current.Snapshot.IsSessionDisconnected.Should().BeFalse();
    }

    [Fact]
    public void EarlierReconnectValidationCannotReleaseANewerReconnect()
    {
        WindowsSystemStateSource source = CreateSource();
        source.ProcessSignal(new(DesktopWindowSignalKind.SessionDisconnected));
        source.ProcessSignal(new(DesktopWindowSignalKind.SessionReconnected));
        long stale = source.Current.Revision;
        source.ProcessSignal(new(DesktopWindowSignalKind.SessionDisconnected));
        source.ProcessSignal(new(DesktopWindowSignalKind.SessionReconnected));
        source.CompleteEnvironmentValidation(true, stale);
        source.Current.Snapshot.IsSessionDisconnected.Should().BeTrue();
        source.CompleteEnvironmentValidation(true, source.Current.Revision);
        source.Current.Snapshot.IsSessionDisconnected.Should().BeFalse();
    }

    [Fact]
    public void NotificationForAnotherSessionDoesNotChangeState()
    {
        WindowsSystemStateSource source = CreateSource();
        source.ProcessSignal(new(DesktopWindowSignalKind.SessionDisconnected,
            SessionId: System.Diagnostics.Process.GetCurrentProcess().SessionId + 1));
        source.Current.Revision.Should().Be(1);
        source.Current.Snapshot.IsSessionDisconnected.Should().BeFalse();
    }

    [Fact]
    public void UnlockRemainsSuspendedUntilShellValidationSucceeds()
    {
        WindowsSystemStateSource source = CreateSource();
        List<SystemStateChange> changes = [];
        source.Changed += (_, args) => changes.Add(args.Current);

        source.ProcessSignal(new DesktopWindowSignal(DesktopWindowSignalKind.SessionLocked));
        source.ProcessSignal(new DesktopWindowSignal(DesktopWindowSignalKind.SessionUnlocked));

        source.Current.Snapshot.IsSessionLocked.Should().BeTrue();
        source.Current.RequiresEnvironmentValidation.Should().BeTrue();
        source.Current.Reason.Should().Be(SystemStateChangeReason.SessionUnlockRequested);

        source.CompleteEnvironmentValidation(shellAvailable: false);
        source.Current.Snapshot.IsSessionLocked.Should().BeTrue();

        source.CompleteEnvironmentValidation(shellAvailable: true);
        source.Current.Snapshot.IsSessionLocked.Should().BeFalse();
        source.Current.Reason.Should().Be(SystemStateChangeReason.SessionUnlocked);
        source.Current.RequiresEnvironmentValidation.Should().BeFalse();
        changes.Select(item => item.Reason).Should().Equal(
            SystemStateChangeReason.SessionLocked,
            SystemStateChangeReason.SessionUnlockRequested,
            SystemStateChangeReason.SessionUnlocked);
    }

    [Fact]
    public void SleepAndDisplayPowerResumeUseTheSameValidationGate()
    {
        WindowsSystemStateSource source = CreateSource();

        source.ProcessSignal(new DesktopWindowSignal(DesktopWindowSignalKind.DisplayPoweredOff));
        source.Current.Snapshot.IsDisplayOff.Should().BeTrue();
        source.ProcessSignal(new DesktopWindowSignal(DesktopWindowSignalKind.DisplayPoweredOn));
        source.Current.Snapshot.IsDisplayOff.Should().BeTrue();
        source.CompleteEnvironmentValidation(shellAvailable: true);
        source.Current.Snapshot.IsDisplayOff.Should().BeFalse();

        source.ProcessSignal(new DesktopWindowSignal(DesktopWindowSignalKind.SystemSuspending));
        source.Current.Snapshot.IsSystemSuspended.Should().BeTrue();
        source.ProcessSignal(new DesktopWindowSignal(DesktopWindowSignalKind.SystemResuming));
        source.Current.Snapshot.IsSystemSuspended.Should().BeTrue();
        source.CompleteEnvironmentValidation(shellAvailable: true);
        source.Current.Snapshot.IsSystemSuspended.Should().BeFalse();
        source.Current.Reason.Should().Be(SystemStateChangeReason.SystemResumed);
    }

    [Fact]
    public void PendingResumeAggregatesNewerSignalsWithoutPublishingAnActiveSnapshot()
    {
        WindowsSystemStateSource source = new(new SystemStateSnapshot(
            IsDisplayOff: true,
            IsSessionLocked: true,
            IsSessionDisconnected: false,
            IsRemoteSession: false,
            IsOnBattery: false,
            HasFullscreenApplication: false,
            IsSystemSuspended: true));

        List<SystemStateChangeReason> reasons = [];
        source.Changed += (_, args) => reasons.Add(args.Current.Reason);
        source.ProcessSignal(new DesktopWindowSignal(DesktopWindowSignalKind.DisplayPoweredOn));
        source.ProcessSignal(new DesktopWindowSignal(DesktopWindowSignalKind.SessionUnlocked));
        source.ProcessSignal(new DesktopWindowSignal(DesktopWindowSignalKind.SystemResuming));

        source.Current.Snapshot.IsDisplayOff.Should().BeTrue();
        source.Current.Snapshot.IsSessionLocked.Should().BeTrue();
        source.Current.Snapshot.IsSystemSuspended.Should().BeTrue();

        source.CompleteEnvironmentValidation(shellAvailable: true);

        source.Current.Snapshot.IsDisplayOff.Should().BeFalse();
        source.Current.Snapshot.IsSessionLocked.Should().BeFalse();
        source.Current.Snapshot.IsSystemSuspended.Should().BeFalse();
        source.Current.Reason.Should().Be(SystemStateChangeReason.SystemResumed);
        reasons.Should().Contain(SystemStateChangeReason.DisplayOn);
        reasons.Should().Contain(SystemStateChangeReason.SessionUnlocked);
        reasons.Should().Contain(SystemStateChangeReason.SystemResumed);
    }

    [Fact]
    public void DuplicateInactiveSignalDoesNotAdvanceRevision()
    {
        WindowsSystemStateSource source = CreateSource();

        source.ProcessSignal(new DesktopWindowSignal(DesktopWindowSignalKind.SessionLocked));
        long revision = source.Current.Revision;
        source.ProcessSignal(new DesktopWindowSignal(DesktopWindowSignalKind.SessionLocked));

        source.Current.Revision.Should().Be(revision);
    }

    private static WindowsSystemStateSource CreateSource() => new(
        new SystemStateSnapshot(false, false, false, false, false, false));

    [Fact]
    public void DisplayOffDoesNotLosePendingUnlockOrForcePlaybackOnValidation()
    {
        WindowsSystemStateSource source = CreateSource();
        source.ProcessSignal(new(DesktopWindowSignalKind.SessionLocked));
        source.ProcessSignal(new(DesktopWindowSignalKind.SessionUnlocked));
        source.ProcessSignal(new(DesktopWindowSignalKind.DisplayPoweredOff));
        source.CompleteEnvironmentValidation(true);

        source.Current.Snapshot.IsSessionLocked.Should().BeFalse();
        source.Current.Snapshot.IsDisplayOff.Should().BeTrue();
        source.Current.Reason.Should().Be(SystemStateChangeReason.SessionUnlocked);
    }

    [Fact]
    public void RelockSupersedesUnlockButPreservesPendingSystemResume()
    {
        WindowsSystemStateSource source = CreateSource();
        source.ProcessSignal(new(DesktopWindowSignalKind.SessionLocked));
        source.ProcessSignal(new(DesktopWindowSignalKind.SystemSuspending));
        source.ProcessSignal(new(DesktopWindowSignalKind.SessionUnlocked));
        source.ProcessSignal(new(DesktopWindowSignalKind.SystemResuming));
        source.ProcessSignal(new(DesktopWindowSignalKind.SessionLocked));
        source.CompleteEnvironmentValidation(true);

        source.Current.Snapshot.IsSessionLocked.Should().BeTrue();
        source.Current.Snapshot.IsSystemSuspended.Should().BeFalse();
        source.Current.Reason.Should().Be(SystemStateChangeReason.SystemResumed);
    }

    [Fact]
    public void DisconnectPreservesUnrelatedPendingResumeAndRemainsInactive()
    {
        WindowsSystemStateSource source = CreateSource();
        source.ProcessSignal(new(DesktopWindowSignalKind.SystemSuspending));
        source.ProcessSignal(new(DesktopWindowSignalKind.SystemResuming));
        source.ProcessSignal(new(DesktopWindowSignalKind.SessionDisconnected));
        source.CompleteEnvironmentValidation(false);
        source.Current.Snapshot.IsSystemSuspended.Should().BeTrue();
        source.CompleteEnvironmentValidation(true);
        source.Current.Snapshot.IsSystemSuspended.Should().BeFalse();
        source.Current.Snapshot.IsSessionDisconnected.Should().BeTrue();
    }

    [Fact]
    public void RelockClearsSupersededValidationEvenWhenPublishedSnapshotWasStillLocked()
    {
        WindowsSystemStateSource source = CreateSource();
        source.ProcessSignal(new(DesktopWindowSignalKind.SessionLocked));
        source.ProcessSignal(new(DesktopWindowSignalKind.SessionUnlocked));
        source.ProcessSignal(new(DesktopWindowSignalKind.SessionLocked));
        source.Current.RequiresEnvironmentValidation.Should().BeFalse();
        source.CompleteEnvironmentValidation(true);
        source.Current.Snapshot.IsSessionLocked.Should().BeTrue();
        source.Current.Reason.Should().Be(SystemStateChangeReason.SessionLocked);
    }
}
