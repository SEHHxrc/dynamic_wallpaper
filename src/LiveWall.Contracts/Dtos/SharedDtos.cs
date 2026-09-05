namespace LiveWall.Contracts.Dtos;

public sealed record RpcErrorDto(
    string Code,
    string Message,
    bool Retryable,
    IReadOnlyDictionary<string, string>? Details = null);

public sealed record WallpaperSummaryDto(
    string Id,
    string Version,
    string Title,
    string Kind,
    string Origin,
    string CompatibilityGrade,
    string? PreviewUri);

public sealed record DisplayDto(
    string Id,
    string Name,
    int X,
    int Y,
    int Width,
    int Height,
    double ScaleFactor,
    bool IsPrimary);

public sealed record AssignmentDto(
    string DisplayId,
    string WallpaperId,
    string LayoutMode,
    string FitMode,
    string? PropertyPresetId);

public sealed record AppStateDto(
    string HostVersion,
    long StateRevision,
    string PlaybackState,
    IReadOnlyList<WallpaperSummaryDto> Wallpapers,
    IReadOnlyList<DisplayDto> Displays,
    IReadOnlyList<AssignmentDto> Assignments);

public sealed record ListWallpapersResultDto(
    IReadOnlyList<WallpaperSummaryDto> Items,
    int Total,
    int Offset,
    int Limit);

public sealed record CompatibilityIssueDto(
    string Code,
    string Severity,
    string Message,
    string? Feature);

public sealed record ProbeImportResultDto(
    bool Recognized,
    string? DetectedFormat,
    string CompatibilityGrade,
    IReadOnlyList<CompatibilityIssueDto> Issues);

public sealed record OperationAcceptedDto(string OperationId);

public sealed record CommandAcceptedDto(bool Accepted, long StateRevision);

public sealed record DiagnosticsDto(
    string HostVersion,
    long UptimeMilliseconds,
    long StateRevision,
    IReadOnlyList<RendererDiagnosticsDto> Renderers,
    IReadOnlyList<string> RecentErrorCodes);

public sealed record RendererDiagnosticsDto(
    string SessionId,
    string RendererId,
    long Generation,
    string State,
    int? ProcessId,
    double? FramesPerSecond,
    long? WorkingSetBytes);
