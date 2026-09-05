namespace LiveWall.Domain.Compatibility;

public enum CompatibilityGrade
{
    Native,
    Full,
    Partial,
    PreviewOnly,
    Unsupported,
}

public sealed record CompatibilityIssue(
    string Code,
    CompatibilitySeverity Severity,
    string Message,
    string? Feature);

public sealed record CompatibilityReport(
    CompatibilityGrade Grade,
    IReadOnlyList<CompatibilityIssue> Issues);

public enum CompatibilitySeverity
{
    Information,
    Warning,
    Error,
}

