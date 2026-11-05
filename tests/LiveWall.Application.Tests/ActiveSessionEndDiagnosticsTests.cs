using FluentAssertions;
using LiveWall.DesktopHostDiagnostics;
using LiveWall.Domain.Displays;
using LiveWall.Host.Orchestration;

namespace LiveWall.Application.Tests;

public sealed class ActiveSessionEndDiagnosticsTests
{
    private static ActiveSessionEndEvidence Complete() => new(1, "run", DateTimeOffset.UtcNow, 7, ["session"], true, 0,
        [new(new("session"), 7, [new DisplayId("display")], 1, TimeSpan.Zero, true, true, true, true, [])],
        [new("session", true, true, null)]);

    [Fact]
    public void CompleteActiveRetirementEvidencePassesWithoutAnyDesktopAccess() => Complete().Valid.Should().BeTrue();

    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate-session")]
    [InlineData("no-commit")]
    [InlineData("owned-surface")]
    [InlineData("missing-retirement")]
    [InlineData("duplicate-retirement")]
    [InlineData("wrong-generation")]
    [InlineData("wrong-session")]
    [InlineData("no-shutdown")]
    [InlineData("no-ack")]
    [InlineData("not-disposed")]
    [InlineData("surface-remains")]
    [InlineData("retirement-issue")]
    [InlineData("missing-process")]
    [InlineData("wrong-process-session")]
    [InlineData("process-alive")]
    [InlineData("handle-open")]
    [InlineData("process-issue")]
    public void IncompleteActiveEvidenceFailsClosed(string mutation)
    {
        ActiveSessionEndEvidence value = Complete();
        SessionRetirementResult retired = value.Retirement[0];
        RendererCleanupEvidence process = value.RendererCleanup[0];
        ActiveSessionEndEvidence broken = mutation switch
        {
            "empty" => value with { ExpectedSessionIds = [], Retirement = [], RendererCleanup = [] },
            "duplicate-session" => value with { ExpectedSessionIds = ["session", "session"] },
            "no-commit" => value with { InitialCommitSucceeded = false },
            "owned-surface" => value with { OwnedSurfaceCount = 1 },
            "missing-retirement" => value with { Retirement = [] },
            "duplicate-retirement" => value with { Retirement = [retired, retired] },
            "wrong-generation" => value with { Retirement = [retired with { Generation = 6 }] },
            "wrong-session" => value with { Retirement = [retired with { SessionId = new("other") }] },
            "no-shutdown" => value with { Retirement = [retired with { ShutdownSent = false }] },
            "no-ack" => value with { Retirement = [retired with { ShutdownCompleted = false }] },
            "not-disposed" => value with { Retirement = [retired with { RendererDisposed = false }] },
            "surface-remains" => value with { Retirement = [retired with { SurfacesCleaned = false }] },
            "retirement-issue" => value with { Retirement = [retired with { Issues = [new(SessionRetirementIssueKind.ShutdownTimeout, "synthetic")] }] },
            "missing-process" => value with { RendererCleanup = [] },
            "wrong-process-session" => value with { RendererCleanup = [process with { SessionId = "other" }] },
            "process-alive" => value with { RendererCleanup = [process with { ExitObserved = false }] },
            "handle-open" => value with { RendererCleanup = [process with { HandleDisposed = false }] },
            "process-issue" => value with { RendererCleanup = [process with { Issue = "synthetic" }] },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        broken.Valid.Should().BeFalse();
    }

    [Fact]
    public void ExplicitDiagnosticArgumentsResolveIsolatedRunDirectory() =>
        ActiveSessionEndOptions.Parse([ActiveSessionEndOptions.Mode, "--run-directory", "artifacts/isolated-test"])
            .RunDirectory.Should().Be(Path.GetFullPath("artifacts/isolated-test"));

    [Theory]
    [InlineData("missing-path")]
    [InlineData("production-flag")]
    [InlineData("duplicate-mode")]
    public void InvalidArgumentsCannotStartAnyDesktopWork(string mutation)
    {
        string[] args = mutation switch
        {
            "missing-path" => [ActiveSessionEndOptions.Mode],
            "production-flag" => [ActiveSessionEndOptions.Mode, "--run-directory", "x", "--allow-raised"],
            _ => [ActiveSessionEndOptions.Mode, ActiveSessionEndOptions.Mode, "--run-directory", "x"],
        };
        Action parse = () => ActiveSessionEndOptions.Parse(args);
        parse.Should().Throw<ArgumentException>();
    }
}
