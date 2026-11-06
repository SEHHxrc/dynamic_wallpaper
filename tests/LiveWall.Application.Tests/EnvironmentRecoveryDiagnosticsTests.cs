using System.Text.Json;
using FluentAssertions;
using LiveWall.Host.Orchestration;

namespace LiveWall.Application.Tests;

public sealed class EnvironmentRecoveryDiagnosticsTests
{
    [Theory]
    [InlineData(true, true, false, EnvironmentRecoveryClassification.ValidSuccess)]
    [InlineData(false, true, false, EnvironmentRecoveryClassification.EnvironmentInconclusive)]
    [InlineData(true, false, false, EnvironmentRecoveryClassification.ProductFailure)]
    [InlineData(false, false, false, EnvironmentRecoveryClassification.ProductFailure)]
    [InlineData(true, true, true, EnvironmentRecoveryClassification.HarnessFailure)]
    [InlineData(false, true, true, EnvironmentRecoveryClassification.HarnessFailure)]
    public void SuccessRequiresOverlapAndAllInvariants(bool overlap, bool valid, bool overflow,
        EnvironmentRecoveryClassification expected) =>
        WindowsHostEventChainDiagnostics.ClassifyEnvironmentRecovery(overlap, valid, overflow).Should().Be(expected);

    [Theory]
    [InlineData(true, DisplayReconciliationTerminalState.Cancelled, ApplyFailureReason.Superseded,
        DisplayReconciliationSupersessionCause.ShellGenerationChanged, true)]
    [InlineData(false, DisplayReconciliationTerminalState.Cancelled, ApplyFailureReason.Superseded,
        DisplayReconciliationSupersessionCause.ShellGenerationChanged, false)]
    [InlineData(true, DisplayReconciliationTerminalState.Succeeded, ApplyFailureReason.None,
        DisplayReconciliationSupersessionCause.ShellGenerationChanged, false)]
    [InlineData(true, DisplayReconciliationTerminalState.Cancelled, ApplyFailureReason.Superseded,
        DisplayReconciliationSupersessionCause.NewerTopology, false)]
    [InlineData(true, DisplayReconciliationTerminalState.Cancelled, ApplyFailureReason.Superseded,
        DisplayReconciliationSupersessionCause.None, false)]
    public void OnlyConfirmedShellPreemptionProvesOverlap(bool pending,
        DisplayReconciliationTerminalState state, ApplyFailureReason reason,
        DisplayReconciliationSupersessionCause cause, bool expected)
    {
        DisplayReconciliationResult result = new(2, 3, state, DisplayReconciliationKind.Reconfigure,
            reason, "This wording is intentionally unrelated to Shell preemption.", TimeSpan.Zero, [], cause);
        WindowsHostEventChainDiagnostics.HasShellTopologyOverlap(result, pending).Should().Be(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    [InlineData(-1073741510)]
    public async Task TrackerVerifiesExitAndDoesNotPersistCredentialsOrNativeIdentity(int exitCode)
    {
        FakeHandle inner = new() { Exited = true, ReportedExitCode = exitCode };
        DiagnosticRendererProcessTracker tracker = new(new FakeLauncher(inner));
        IRendererProcessHandle wrapped = tracker.Start(new("test.exe", "secret-pipe", "logical-session", "secret-token"));
        tracker.Snapshot().Single().ExitObserved.Should().BeFalse();
        wrapped.Id.Should().Be(12345);
        await wrapped.DisposeAsync();
        tracker.Snapshot().Single().Should().Be(new RendererCleanupEvidence("logical-session", true, true, null, exitCode));
        inner.Disposed.Should().BeTrue();
        string serialized = JsonSerializer.Serialize(tracker.Snapshot());
        serialized.Should().NotContain("secret-pipe").And.NotContain("secret-token").And.NotContain("12345");
    }

    [Fact]
    public async Task TrackerCannotCallHandleDisposalProofOfProcessExit()
    {
        FakeHandle inner = new();
        DiagnosticRendererProcessTracker tracker = new(new FakeLauncher(inner), TimeSpan.FromMilliseconds(10));
        IRendererProcessHandle wrapped = tracker.Start(new("test.exe", "pipe", "session", "token"));
        await wrapped.DisposeAsync();
        RendererCleanupEvidence evidence = tracker.Snapshot().Single();
        evidence.ExitObserved.Should().BeFalse();
        evidence.HandleDisposed.Should().BeTrue();
        evidence.Issue.Should().NotBeNull();
        inner.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task TrackerDisposalFailureDoesNotClaimCleanup()
    {
        FakeHandle inner = new() { Exited = true, FailDispose = true };
        DiagnosticRendererProcessTracker tracker = new(new FakeLauncher(inner));
        IRendererProcessHandle wrapped = tracker.Start(new("test.exe", "pipe", "session", "token"));
        Func<Task> dispose = async () => await wrapped.DisposeAsync();
        await dispose.Should().ThrowAsync<InvalidOperationException>();
        tracker.Snapshot().Single().HandleDisposed.Should().BeFalse();
    }

    [Fact]
    public void IncompleteExperimentRetainsPhaseAndCause()
    {
        EnvironmentRecoveryDiagnosticException exception = new("AwaitingTopologyChange", new TimeoutException());
        exception.Phase.Should().Be("AwaitingTopologyChange");
        exception.InnerException.Should().BeOfType<TimeoutException>();
    }

    private sealed class FakeLauncher(FakeHandle handle) : IRendererProcessLauncher
    {
        public IRendererProcessHandle Start(RendererProcessStartRequest request) => handle;
    }

    private sealed class FakeHandle : IRendererProcessHandle
    {
        public bool Exited { get; set; }
        public bool Disposed { get; private set; }
        public bool FailDispose { get; init; }
        public int ReportedExitCode { get; init; }
        public int Id => 12345;
        public bool HasExited => Exited;
        public int? ExitCode => Exited ? ReportedExitCode : null;
        public Task WaitForExitAsync(CancellationToken token) => Exited ? Task.CompletedTask : Task.Delay(Timeout.Infinite, token);
        public void Terminate() => Exited = true;
        public ValueTask DisposeAsync()
        {
            if (FailDispose) throw new InvalidOperationException("Disposal failed.");
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
