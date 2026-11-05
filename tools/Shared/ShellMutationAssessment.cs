using LiveWall.Platform.Windows.Desktop;

namespace LiveWall.Diagnostics;

internal enum ShellMutationRecovery { NotRequired, Recovered, CleanupIncomplete, Inconclusive }
internal sealed record ShellMutationAssessment(ShellMutationRecovery Recovery, int BeforeCount, int AfterCount, int AddedCount)
{
    public static ShellMutationAssessment Evaluate(ShellTopologySnapshot before, ShellTopologySnapshot? requested,
        ShellTopologySnapshot? after, bool requestAttempted)
    {
        static Dictionary<ulong, ShellWindowFingerprint> Workers(ShellTopologySnapshot snapshot) => snapshot.ShellDescendants
            .Where(window => window.ParentWindowHandle == snapshot.ShellWindowHandle && window.ClassName == "WorkerW")
            .ToDictionary(window => window.WindowHandle);
        Dictionary<ulong, ShellWindowFingerprint> baseline = Workers(before);
        Dictionary<ulong, ShellWindowFingerprint> final = after is null ? [] : Workers(after);
        int added = final.Keys.Except(baseline.Keys).Count();
        ShellMutationAssessment Result(ShellMutationRecovery status) => new(status, baseline.Count, final.Count, added);
        if (before.Availability != ShellTopologyAvailability.Available || after?.Availability != ShellTopologyAvailability.Available ||
            before.ShellWindowHandle != after.ShellWindowHandle ||
            before.TopLevelWindows.FirstOrDefault(window => window.WindowHandle == before.ShellWindowHandle)?.ProcessId is not { } pid ||
            after.TopLevelWindows.FirstOrDefault(window => window.WindowHandle == after.ShellWindowHandle)?.ProcessId != pid)
            return Result(ShellMutationRecovery.Inconclusive);
        if (added != 0) return Result(ShellMutationRecovery.CleanupIncomplete);
        if (baseline.Count != final.Count || baseline.Any(pair => !final.TryGetValue(pair.Key, out var other) ||
            pair.Value.ProcessId != other.ProcessId || pair.Value.OwnerWindowHandle != other.OwnerWindowHandle ||
            pair.Value.Bounds != other.Bounds || pair.Value.Style != other.Style || pair.Value.ExtendedStyle != other.ExtendedStyle))
            return Result(ShellMutationRecovery.Inconclusive);
        if (requested is null) return Result(requestAttempted ? ShellMutationRecovery.Inconclusive : ShellMutationRecovery.NotRequired);
        if (requested.Availability != ShellTopologyAvailability.Available || requested.ShellWindowHandle != before.ShellWindowHandle ||
            requested.TopLevelWindows.FirstOrDefault(window => window.WindowHandle == requested.ShellWindowHandle)?.ProcessId != pid)
            return Result(ShellMutationRecovery.Inconclusive);
        return Result(Workers(requested).Keys.Except(baseline.Keys).Any() ? ShellMutationRecovery.Recovered : ShellMutationRecovery.NotRequired);
    }
}
