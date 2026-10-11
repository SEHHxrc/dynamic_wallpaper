using FluentAssertions;
using LiveWall.Host.Orchestration;

namespace LiveWall.Application.Tests;

public sealed class SessionReconnectDiagnosticsTests
{
    private static SessionNotificationEvidence Signal(long sequence, string kind,
        bool matches = true, bool stopping = false, long revision = 2) =>
        new(sequence, kind, matches, revision, stopping, TimeSpan.Zero);

    [Theory]
    [InlineData("Resume")]
    [InlineData("Reconfigure")]
    [InlineData("Rebuild")]
    public void OrderedCurrentSessionNotificationsAndSuccessfulTerminalAreRequired(string path)
    {
        WindowsHostEventChainDiagnostics.HasValidatedSessionReconnect(
            new(3, "Succeeded", path, "None", TimeSpan.FromMilliseconds(350)),
            [Signal(1, "SessionDisconnected"), Signal(2, "SessionReconnected")])
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("SessionLocked", "SessionUnlocked", true, false, 1, 2, 2)]
    [InlineData("SessionDisconnected", "SessionReconnected", false, false, 1, 2, 2)]
    [InlineData("SessionDisconnected", "SessionReconnected", true, true, 1, 2, 2)]
    [InlineData("SessionDisconnected", "SessionReconnected", true, false, 2, 1, 2)]
    [InlineData("SessionDisconnected", "SessionReconnected", true, false, 1, 2, 3)]
    public void InvalidNotificationsCannotPass(string before, string after, bool matches,
        bool stopping, long firstSequence, long secondSequence, long revision)
    {
        WindowsHostEventChainDiagnostics.HasValidatedSessionReconnect(
            new(3, "Succeeded", "Resume", "None", TimeSpan.Zero),
            [Signal(firstSequence, before, matches, stopping, revision),
                Signal(secondSequence, after, matches, stopping, revision)])
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("Failed", "Resume", "ValidationTimeout")]
    [InlineData("Cancelled", "Resume", "SessionDisconnected")]
    [InlineData("Succeeded", null, "None")]
    [InlineData("Succeeded", "Unknown", "None")]
    [InlineData("Succeeded", "Resume", "RecoveryFailed")]
    public void InvalidTerminalCannotPass(string state, string? path, string reason)
    {
        WindowsHostEventChainDiagnostics.HasValidatedSessionReconnect(
            new(3, state, path, reason, TimeSpan.Zero),
            [Signal(1, "SessionDisconnected"), Signal(2, "SessionReconnected")])
            .Should().BeFalse();
    }

    [Fact]
    public void EmptyEvidenceCannotPass()
    {
        WindowsHostEventChainDiagnostics.HasValidatedSessionReconnect(null, []).Should().BeFalse();
        WindowsHostEventChainDiagnostics.HasValidatedSessionReconnect(
            new(3, "Succeeded", "Resume", "None", TimeSpan.Zero), []).Should().BeFalse();
    }

    [Fact]
    public void FailurePreservesPhaseAndTerminalWithoutClaimingCleanup()
    {
        SessionReconnectEvidence terminal = new(3, "Failed", null, "ValidationTimeout", TimeSpan.Zero);
        SystemTransitionDiagnosticException exception = new("EnvironmentValidation",
            new TimeoutException(), terminal, [Signal(1, "SessionDisconnected")]);
        exception.Phase.Should().Be("EnvironmentValidation");
        exception.SessionReconnect.Should().Be(terminal);
        exception.Notifications.Should().ContainSingle();
        exception.InnerException.Should().BeOfType<TimeoutException>();
    }
}
