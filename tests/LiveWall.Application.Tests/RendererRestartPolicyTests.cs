using FluentAssertions;
using LiveWall.Application.Sessions;

namespace LiveWall.Application.Tests;

public sealed class RendererRestartPolicyTests
{
    private readonly DateTimeOffset now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 2)]
    [InlineData(3, 8)]
    public void FirstThreeRecentFailuresUseExpectedBackoff(int failureCount, int delaySeconds)
    {
        DateTimeOffset[] failures = Enumerable.Range(0, failureCount)
            .Select(index => now.AddSeconds(-index))
            .ToArray();

        RendererRestartDecision decision = RendererRestartPolicy.Evaluate(failures, now);

        decision.ShouldRestart.Should().BeTrue();
        decision.Delay.Should().Be(TimeSpan.FromSeconds(delaySeconds));
        decision.CircuitBroken.Should().BeFalse();
    }

    [Fact]
    public void FourthRecentFailureBreaksCircuit()
    {
        DateTimeOffset[] failures = Enumerable.Range(0, 4)
            .Select(index => now.AddSeconds(-index))
            .ToArray();

        RendererRestartDecision decision = RendererRestartPolicy.Evaluate(failures, now);

        decision.ShouldRestart.Should().BeFalse();
        decision.CircuitBroken.Should().BeTrue();
    }

    [Fact]
    public void FailuresOutsideWindowDoNotCount()
    {
        DateTimeOffset[] failures = [now.AddMinutes(-2), now];

        RendererRestartDecision decision = RendererRestartPolicy.Evaluate(failures, now);

        decision.Delay.Should().Be(TimeSpan.Zero);
        decision.CircuitBroken.Should().BeFalse();
    }
}
