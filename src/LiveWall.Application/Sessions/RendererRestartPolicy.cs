namespace LiveWall.Application.Sessions;

public static class RendererRestartPolicy
{
    public static TimeSpan FailureWindow { get; } = TimeSpan.FromSeconds(60);

    public static RendererRestartDecision Evaluate(
        IReadOnlyList<DateTimeOffset> failures,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(failures);
        int recentFailureCount = failures.Count(failure =>
            failure <= now && now - failure <= FailureWindow);

        return recentFailureCount switch
        {
            <= 1 => new RendererRestartDecision(true, TimeSpan.Zero, false),
            2 => new RendererRestartDecision(true, TimeSpan.FromSeconds(2), false),
            3 => new RendererRestartDecision(true, TimeSpan.FromSeconds(8), false),
            _ => new RendererRestartDecision(false, TimeSpan.Zero, true),
        };
    }
}

public sealed record RendererRestartDecision(
    bool ShouldRestart,
    TimeSpan Delay,
    bool CircuitBroken);
