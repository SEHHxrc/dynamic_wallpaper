namespace LiveWall.Diagnostics;

// Resource removal, a shutdown acknowledgment, and a successful process return
// are separate observations. Abnormal exit must still fail candidate acceptance.
internal sealed record VideoCandidateCleanupAssessment(bool OwnedResourcesCleaned, bool GracefulRetirement)
{
    public static VideoCandidateCleanupAssessment Evaluate(
        bool resourcesDisposed,
        bool shutdownCompleted,
        bool ownedWindowsRemain,
        int expectedRendererCount,
        IReadOnlyList<int?> exitCodes)
    {
        bool allExited = expectedRendererCount > 0 && exitCodes.Count == expectedRendererCount &&
            exitCodes.All(code => code.HasValue);
        bool cleaned = resourcesDisposed && !ownedWindowsRemain && allExited;
        return new(cleaned, cleaned && shutdownCompleted && exitCodes.All(code => code == 0));
    }
}
