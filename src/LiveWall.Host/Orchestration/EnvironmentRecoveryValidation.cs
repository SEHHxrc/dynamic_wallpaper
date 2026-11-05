using LiveWall.Domain.Displays;

namespace LiveWall.Host.Orchestration;

public enum EnvironmentRecoveryIssue
{
    ObservationOverflow,
    MissingOrDuplicateTopologyTerminal,
    FinalRevisionNotObserved,
    NoConcurrentPreemption,
    InvalidTerminal,
    InvalidRecoveryTransaction,
    InvalidCommitCount,
    MissingCurrentFirstFrame,
    InvalidDisplayCoverage,
    OwnedResourcesNotCleaned,
    MissingOrInvalidRetirement,
    MissingOrInvalidRendererExit,
}

public sealed record EnvironmentRecoveryValidation(EnvironmentRecoveryClassification Classification,
    IReadOnlyList<EnvironmentRecoveryIssue> Issues);

public static partial class WindowsHostEventChainDiagnostics
{
    // This validator is deliberately independent of native APIs. The experiment and
    // synthetic tests must use the same acceptance rules, not a weaker test-only copy.
    internal static EnvironmentRecoveryValidation ValidateEnvironmentRecovery(
        WindowsHostEnvironmentRecoveryResult result, IReadOnlyList<DisplayId> expectedDisplays)
    {
        List<EnvironmentRecoveryIssue> issues = [];
        if (result.EventOverflow) issues.Add(EnvironmentRecoveryIssue.ObservationOverflow);
        long[] revisions = result.PublishedTopologies.Select(item => item.Revision).ToArray();
        bool terminalsComplete = revisions.Length > 0 && revisions.Distinct().Count() == revisions.Length &&
            revisions.SequenceEqual(revisions.Order()) && revisions.All(revision => revision > result.InitialTopologyRevision) &&
            result.Reconciliation.Count == revisions.Length && revisions.All(revision =>
                result.Reconciliation.Count(item => item.TopologyRevision == revision) == 1);
        if (!terminalsComplete) issues.Add(EnvironmentRecoveryIssue.MissingOrDuplicateTopologyTerminal);
        DisplayTopology? finalTopology = result.PublishedTopologies.Count == 0 ? null : result.PublishedTopologies[^1];
        if (!result.FinalRevisionValidated || finalTopology?.Revision != result.FinalTopologyRevision)
            issues.Add(EnvironmentRecoveryIssue.FinalRevisionNotObserved);

        DisplayReconciliationResult? first = terminalsComplete
            ? result.Reconciliation.Single(item => item.TopologyRevision == revisions[0]) : null;
        bool overlap = first is not null && result.ExplorerRestartInvoked &&
            HasShellTopologyOverlap(first, result.PendingAtRestart);
        if (!overlap) issues.Add(EnvironmentRecoveryIssue.NoConcurrentPreemption);
        if (result.Recovery.Any(item => item.TerminalState != DesktopRecoveryTerminalState.Succeeded ||
                item.FailureReason != ApplyFailureReason.None) ||
            result.Reconciliation.Any(item => item.TerminalState switch
            {
                DisplayReconciliationTerminalState.Succeeded => item.FailureReason != ApplyFailureReason.None,
                DisplayReconciliationTerminalState.Cancelled => item.FailureReason != ApplyFailureReason.Superseded ||
                    item.SupersessionCause == DisplayReconciliationSupersessionCause.None,
                _ => true,
            })) issues.Add(EnvironmentRecoveryIssue.InvalidTerminal);

        if (result.ExplorerRestartInvoked && (result.Recovery.Count != 1 || result.ValidatedShellChanges != 1 ||
            result.ForwardedRecoveryCommands != 1 || result.Recovery[0].FinalHostGeneration != result.FinalGeneration ||
            result.Recovery[0].CompletionMode is not (DesktopRecoveryCompletionMode.Reattached or DesktopRecoveryCompletionMode.Rebuilt)))
            issues.Add(EnvironmentRecoveryIssue.InvalidRecoveryTransaction);
        if (overlap)
        {
            DesktopRecoveryTimelineEntry[] commits = result.Recovery.SelectMany(item => item.Timeline)
                .Where(item => item.Phase == DesktopRecoveryPhase.SurfacesReplaced && item.Outcome == ApplyStageOutcome.Success)
                .ToArray();
            if (result.BatchReplaceCount != 1 || commits.Length != 1 ||
                commits[0].HostGeneration != result.FinalGeneration ||
                result.Reconciliation.Any(item => item.TerminalState == DisplayReconciliationTerminalState.Cancelled &&
                    item.Timeline.Any(entry => entry.Phase == DisplayReconciliationPhase.SurfacesReplaced &&
                        entry.Outcome == ApplyStageOutcome.Success)))
                issues.Add(EnvironmentRecoveryIssue.InvalidCommitCount);
            if (result.FinalSurfaces.Any(surface => result.Recovery.SelectMany(item => item.Timeline).Count(entry =>
                entry.Phase == DesktopRecoveryPhase.FirstFramePresented && entry.Outcome == ApplyStageOutcome.Success &&
                entry.HostGeneration == result.FinalGeneration && entry.SurfaceOrdinal == surface.SurfaceOrdinal &&
                entry.DisplayIds.OrderBy(id => id.Value).SequenceEqual(surface.Displays.OrderBy(id => id.Value)) &&
                commits.Length == 1 && entry.Elapsed <= commits[0].Elapsed) != 1))
                issues.Add(EnvironmentRecoveryIssue.MissingCurrentFirstFrame);
        }

        EnvironmentSurfaceEvidence[] surfaces = result.FinalSurfaces.ToArray();
        bool coverageValid = expectedDisplays.Count > 0 && expectedDisplays.Distinct().Count() == expectedDisplays.Count &&
            surfaces.Length == expectedDisplays.Count && surfaces.All(surface => surface.Displays.Count == 1) &&
            surfaces.SelectMany(surface => surface.Displays).OrderBy(id => id.Value)
                .SequenceEqual(expectedDisplays.OrderBy(id => id.Value)) &&
            surfaces.Select(surface => surface.SessionId).Distinct(StringComparer.Ordinal).Count() == surfaces.Length &&
            surfaces.All(surface => surface.SurfaceOrdinal > 0) &&
            surfaces.Select(surface => surface.SurfaceOrdinal).Distinct().Count() == surfaces.Length &&
            surfaces.All(surface => finalTopology is not null && finalTopology.Displays.Any(display =>
                display.Id == surface.Displays[0] && display.Bounds == surface.Bounds));
        if (!coverageValid) issues.Add(EnvironmentRecoveryIssue.InvalidDisplayCoverage);
        if (!result.OwnedResourcesCleaned) issues.Add(EnvironmentRecoveryIssue.OwnedResourcesNotCleaned);
        if (result.Retirement.Count == 0 || result.Retirement.Any(item => !item.OwnedResourcesCleaned) ||
            surfaces.Any(surface => result.Retirement.Count(item => item.SessionId.Value == surface.SessionId &&
                item.Generation == result.FinalGeneration && item.ShutdownSent && item.ShutdownCompleted &&
                item.Issues.Count == 0) != 1))
            issues.Add(EnvironmentRecoveryIssue.MissingOrInvalidRetirement);
        if (result.RendererCleanup.Count == 0 || result.RendererCleanup.Any(item =>
                !item.ExitObserved || !item.HandleDisposed || item.Issue is not null) ||
            surfaces.Any(surface => result.RendererCleanup.Count(item => item.SessionId == surface.SessionId) != 1))
            issues.Add(EnvironmentRecoveryIssue.MissingOrInvalidRendererExit);

        bool harnessFailure = issues.Contains(EnvironmentRecoveryIssue.ObservationOverflow) ||
            issues.Contains(EnvironmentRecoveryIssue.MissingOrDuplicateTopologyTerminal);
        bool productFailure = issues.Any(issue => issue is not (EnvironmentRecoveryIssue.ObservationOverflow or
            EnvironmentRecoveryIssue.MissingOrDuplicateTopologyTerminal or EnvironmentRecoveryIssue.NoConcurrentPreemption or
            EnvironmentRecoveryIssue.FinalRevisionNotObserved));
        EnvironmentRecoveryClassification classification = harnessFailure ? EnvironmentRecoveryClassification.HarnessFailure :
            productFailure ? EnvironmentRecoveryClassification.ProductFailure :
            issues.Count == 0 ? EnvironmentRecoveryClassification.ValidSuccess : EnvironmentRecoveryClassification.EnvironmentInconclusive;
        return new(classification, Array.AsReadOnly(issues.ToArray()));
    }
}
