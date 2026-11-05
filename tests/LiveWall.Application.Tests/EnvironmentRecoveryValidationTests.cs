using FluentAssertions;
using LiveWall.Application.Layouts;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Sessions;
using LiveWall.Host.Orchestration;

namespace LiveWall.Application.Tests;

public sealed class EnvironmentRecoveryValidationTests
{
    private static readonly DisplayId[] Targets = [new("primary"), new("external")];

    [Theory]
    [InlineData(DesktopRecoveryCompletionMode.Reattached)]
    [InlineData(DesktopRecoveryCompletionMode.Rebuilt)]
    public void CompleteTwoDisplayEvidencePassesWithoutNativeDesktop(DesktopRecoveryCompletionMode mode)
    {
        WindowsHostEnvironmentRecoveryResult sample = CompleteSample();
        sample = sample with { Recovery = [sample.Recovery[0] with { CompletionMode = mode }] };
        EnvironmentRecoveryValidation validation = Validate(sample);
        validation.Classification.Should().Be(EnvironmentRecoveryClassification.ValidSuccess);
        validation.Issues.Should().BeEmpty();
    }

    [Theory]
    [InlineData("duplicate-display", EnvironmentRecoveryIssue.InvalidDisplayCoverage)]
    [InlineData("wrong-bounds", EnvironmentRecoveryIssue.InvalidDisplayCoverage)]
    [InlineData("duplicate-session", EnvironmentRecoveryIssue.InvalidDisplayCoverage)]
    [InlineData("duplicate-ordinal", EnvironmentRecoveryIssue.InvalidDisplayCoverage)]
    [InlineData("missing-surface", EnvironmentRecoveryIssue.InvalidDisplayCoverage)]
    [InlineData("missing-retirement", EnvironmentRecoveryIssue.MissingOrInvalidRetirement)]
    [InlineData("duplicate-retirement", EnvironmentRecoveryIssue.MissingOrInvalidRetirement)]
    [InlineData("old-retirement", EnvironmentRecoveryIssue.MissingOrInvalidRetirement)]
    [InlineData("ungraceful-retirement", EnvironmentRecoveryIssue.MissingOrInvalidRetirement)]
    [InlineData("missing-exit", EnvironmentRecoveryIssue.MissingOrInvalidRendererExit)]
    [InlineData("duplicate-exit", EnvironmentRecoveryIssue.MissingOrInvalidRendererExit)]
    [InlineData("handle-only", EnvironmentRecoveryIssue.MissingOrInvalidRendererExit)]
    [InlineData("owned-resources", EnvironmentRecoveryIssue.OwnedResourcesNotCleaned)]
    [InlineData("double-commit", EnvironmentRecoveryIssue.InvalidCommitCount)]
    [InlineData("cancelled-commit", EnvironmentRecoveryIssue.InvalidCommitCount)]
    [InlineData("missing-frame", EnvironmentRecoveryIssue.MissingCurrentFirstFrame)]
    [InlineData("stale-frame", EnvironmentRecoveryIssue.MissingCurrentFirstFrame)]
    [InlineData("duplicate-frame", EnvironmentRecoveryIssue.MissingCurrentFirstFrame)]
    [InlineData("frame-after-commit", EnvironmentRecoveryIssue.MissingCurrentFirstFrame)]
    [InlineData("failed-recovery", EnvironmentRecoveryIssue.InvalidTerminal)]
    [InlineData("duplicate-recovery", EnvironmentRecoveryIssue.InvalidRecoveryTransaction)]
    [InlineData("unknown-mode", EnvironmentRecoveryIssue.InvalidRecoveryTransaction)]
    public void IncompleteOrContradictoryProductEvidenceCannotPass(string mutation, EnvironmentRecoveryIssue issue)
    {
        WindowsHostEnvironmentRecoveryResult s = CompleteSample();
        DesktopRecoveryResult recovery = s.Recovery[0];
        WindowsHostEnvironmentRecoveryResult broken = mutation switch
        {
            "duplicate-display" => s with { FinalSurfaces = [s.FinalSurfaces[0], s.FinalSurfaces[1] with { Displays = [Targets[0]] }] },
            "wrong-bounds" => s with { FinalSurfaces = [s.FinalSurfaces[0], s.FinalSurfaces[1] with { Bounds = new(0, 0, 100, 100) }] },
            "duplicate-session" => s with { FinalSurfaces = [s.FinalSurfaces[0], s.FinalSurfaces[1] with { SessionId = "session-1" }] },
            "duplicate-ordinal" => s with { FinalSurfaces = [s.FinalSurfaces[0], s.FinalSurfaces[1] with { SurfaceOrdinal = 1 }] },
            "missing-surface" => s with { FinalSurfaces = [s.FinalSurfaces[0]] },
            "missing-retirement" => s with { Retirement = [s.Retirement[0]] },
            "duplicate-retirement" => s with { Retirement = [.. s.Retirement, s.Retirement[0]] },
            "old-retirement" => s with { Retirement = [s.Retirement[0], s.Retirement[1] with { Generation = 1 }] },
            "ungraceful-retirement" => s with { Retirement = [s.Retirement[0], s.Retirement[1] with { ShutdownCompleted = false }] },
            "missing-exit" => s with { RendererCleanup = [s.RendererCleanup[0]] },
            "duplicate-exit" => s with { RendererCleanup = [.. s.RendererCleanup, s.RendererCleanup[0]] },
            "handle-only" => s with { RendererCleanup = [s.RendererCleanup[0], s.RendererCleanup[1] with { ExitObserved = false }] },
            "owned-resources" => s with { OwnedResourcesCleaned = false },
            "double-commit" => s with { BatchReplaceCount = 2 },
            "cancelled-commit" => s with { Reconciliation = [s.Reconciliation[0] with { Timeline =
                [new(2, 2, DisplayReconciliationPhase.SurfacesReplaced, ApplyStageOutcome.Success,
                    ApplyFailureReason.None, TimeSpan.Zero, Targets, null)] }] },
            "missing-frame" => s with { Recovery = [recovery with { Timeline = recovery.Timeline.Where(e => e.SurfaceOrdinal != 2).ToArray() }] },
            "stale-frame" => s with { Recovery = [recovery with { Timeline = recovery.Timeline.Select(e =>
                e.SurfaceOrdinal == 2 ? e with { HostGeneration = 1 } : e).ToArray() }] },
            "duplicate-frame" => s with { Recovery = [recovery with { Timeline = [.. recovery.Timeline, recovery.Timeline[0]] }] },
            "frame-after-commit" => s with { Recovery = [recovery with { Timeline = recovery.Timeline.Select(e =>
                e.SurfaceOrdinal == 2 ? e with { Elapsed = TimeSpan.FromSeconds(1) } : e).ToArray() }] },
            "failed-recovery" => s with { Recovery = [recovery with { TerminalState = DesktopRecoveryTerminalState.CircuitBroken }] },
            "duplicate-recovery" => s with { Recovery = [recovery, recovery] },
            "unknown-mode" => s with { Recovery = [recovery with { CompletionMode = null }] },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        EnvironmentRecoveryValidation validation = Validate(broken);
        validation.Classification.Should().Be(EnvironmentRecoveryClassification.ProductFailure);
        validation.Issues.Should().Contain(issue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrDuplicatePublishedRevisionTerminalIsHarnessFailure(bool duplicate)
    {
        WindowsHostEnvironmentRecoveryResult s = CompleteSample();
        Validate(s with { Reconciliation = duplicate ? [s.Reconciliation[0], s.Reconciliation[0]] : [] })
            .Classification.Should().Be(EnvironmentRecoveryClassification.HarnessFailure);
    }

    [Fact]
    public void SerialSuccessfulRunIsInconclusiveNotConcurrentAcceptance()
    {
        WindowsHostEnvironmentRecoveryResult s = CompleteSample();
        s = s with { PendingAtRestart = false, OverlapProven = true, ExplorerRestartInvoked = false,
            Recovery = [], ValidatedShellChanges = 0, ForwardedRecoveryCommands = 0,
            Reconciliation = [s.Reconciliation[0] with { TerminalState = DisplayReconciliationTerminalState.Succeeded,
                FailureReason = ApplyFailureReason.None, SupersessionCause = DisplayReconciliationSupersessionCause.None }] };
        Validate(s).Classification.Should().Be(EnvironmentRecoveryClassification.EnvironmentInconclusive,
            "the validator must derive overlap instead of trusting the caller's OverlapProven flag");
    }

    [Fact]
    public void UnobservedFinalRevisionCannotPassEvenWhenEverythingElseSucceeded()
    {
        WindowsHostEnvironmentRecoveryResult s = CompleteSample();
        Validate(s with { FinalTopologyRevision = 3, FinalRevisionValidated = false }).Classification
            .Should().Be(EnvironmentRecoveryClassification.EnvironmentInconclusive);
    }

    [Fact]
    public void NoDisplaysCannotVacuouslyPassThisPresentationExperiment()
    {
        WindowsHostEnvironmentRecoveryResult s = CompleteSample();
        WindowsHostEventChainDiagnostics.ValidateEnvironmentRecovery(s with { FinalSurfaces = [] }, []).Issues
            .Should().Contain(EnvironmentRecoveryIssue.InvalidDisplayCoverage);
    }

    [Fact]
    public async Task PublishedTopologyHasExactlyOneTerminalPerRevision()
    {
        DisplayReconciliationOperation operation = new(2, DisplayReconciliationKind.Reconfigure);
        operation.Complete(DisplayReconciliationTerminalState.Cancelled, 2, ApplyFailureReason.Superseded,
            "First terminal", DisplayReconciliationSupersessionCause.ShellGenerationChanged);
        operation.Complete(DisplayReconciliationTerminalState.Succeeded, 3);
        DisplayReconciliationResult result = await operation.Completion;
        result.SupersessionCause.Should().Be(DisplayReconciliationSupersessionCause.ShellGenerationChanged);
        result.TerminalState.Should().Be(DisplayReconciliationTerminalState.Cancelled);
    }

    private static EnvironmentRecoveryValidation Validate(WindowsHostEnvironmentRecoveryResult sample) =>
        WindowsHostEventChainDiagnostics.ValidateEnvironmentRecovery(sample, Targets);

    private static WindowsHostEnvironmentRecoveryResult CompleteSample()
    {
        DisplayTopology topology = new(2, [new(Targets[0], "main", new(0, 0, 1920, 1080), 2, 60, true),
            new(Targets[1], "external", new(-2560, 0, 2560, 1440), 1.5, 60, false)]);
        EnvironmentSurfaceEvidence[] surfaces = topology.Displays.Select((display, index) =>
            new EnvironmentSurfaceEvidence($"session-{index + 1}", index + 1, [display.Id], display.Bounds)).ToArray();
        DesktopRecoveryTimelineEntry[] frames = surfaces.Select(surface => new DesktopRecoveryTimelineEntry(3, 4, 1,
            surface.SurfaceOrdinal, surface.Displays, DesktopRecoveryPhase.FirstFramePresented,
            ApplyStageOutcome.Success, ApplyFailureReason.None, TimeSpan.Zero, null)).ToArray();
        DesktopRecoveryResult recovery = new(3, 4, DesktopRecoveryTerminalState.Succeeded, DesktopRecoveryCompletionMode.Rebuilt,
            ApplyFailureReason.None, null, TimeSpan.Zero, [.. frames,
                new(3, 4, 1, 0, Targets, DesktopRecoveryPhase.SurfacesReplaced,
                    ApplyStageOutcome.Success, ApplyFailureReason.None, TimeSpan.Zero, null)]);
        SessionRetirementResult[] retirement = surfaces.Select(surface => new SessionRetirementResult(new SessionId(surface.SessionId),
            4, surface.Displays, surface.SurfaceOrdinal, TimeSpan.Zero, true, true, true, true, [])).ToArray();
        return new(EnvironmentRecoveryClassification.EnvironmentInconclusive, true, true, true, 1, 4, 1, 2, 1, 1, 1,
            [topology], [new(2, 2, DisplayReconciliationTerminalState.Cancelled,
                DisplayReconciliationKind.Reconfigure, ApplyFailureReason.Superseded,
                "Text does not participate in acceptance.", TimeSpan.Zero, [], DisplayReconciliationSupersessionCause.ShellGenerationChanged)],
            [recovery], surfaces, retirement, surfaces.Select(surface => new RendererCleanupEvidence(surface.SessionId, true, true, null)).ToArray(),
            true, false, true);
    }
}
