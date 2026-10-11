using System.Text.Json;
using FluentAssertions;
using LiveWall.Application.Sessions;
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
        "Display preparation was superseded by a confirmed Shell generation change.", true)]
    [InlineData(false, DisplayReconciliationTerminalState.Cancelled, ApplyFailureReason.Superseded,
        "confirmed Shell generation", false)]
    [InlineData(true, DisplayReconciliationTerminalState.Succeeded, ApplyFailureReason.None,
        "confirmed Shell generation", false)]
    [InlineData(true, DisplayReconciliationTerminalState.Cancelled, ApplyFailureReason.Superseded,
        "Higher topology revision", false)]
    [InlineData(true, DisplayReconciliationTerminalState.Cancelled, ApplyFailureReason.Superseded, null, false)]
    public void OnlyConfirmedShellPreemptionProvesOverlap(bool pending,
        DisplayReconciliationTerminalState state, ApplyFailureReason reason, string? detail, bool expected)
    {
        DisplayReconciliationResult result = new(2, 3, state, DisplayReconciliationKind.Reconfigure,
            reason, detail, TimeSpan.Zero, []);
        WindowsHostEventChainDiagnostics.HasShellTopologyOverlap(result, pending).Should().Be(expected);
    }

    [Fact]
    public async Task TrackerVerifiesExitAndDoesNotPersistCredentialsOrNativeIdentity()
    {
        FakeHandle inner = new() { Exited = true };
        DiagnosticRendererProcessTracker tracker = new(new FakeLauncher(inner));
        IRendererProcessHandle wrapped = tracker.Start(new("test.exe", "secret-pipe", "logical-session", "secret-token"));
        tracker.Snapshot().Single().ExitObserved.Should().BeFalse();
        wrapped.Id.Should().Be(12345);
        await wrapped.DisposeAsync();
        tracker.Snapshot().Single().Should().Be(new RendererCleanupEvidence("logical-session", true, true, null));
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
        public int Id => 12345;
        public bool HasExited => Exited;
        public int? ExitCode => Exited ? 0 : null;
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
