using FluentAssertions;
using LiveWall.Application.Policies;
using LiveWall.Domain.Playback;
using LiveWall.Platform.Windows.Desktop;
using LiveWall.Platform.Windows.Power;

namespace LiveWall.Windows.IntegrationTests;

public sealed class WindowsSystemStateSourceTests
{
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
}
