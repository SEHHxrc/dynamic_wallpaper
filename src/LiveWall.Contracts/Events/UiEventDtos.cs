namespace LiveWall.Contracts.Events;

using LiveWall.Contracts.Dtos;

public sealed record AppStateChangedDto(AppStateDto State);

public sealed record LibraryChangedDto(IReadOnlyList<WallpaperSummaryDto> Wallpapers);

public sealed record ImportProgressDto(
    string OperationId,
    string Stage,
    double Fraction,
    string? Message);

public sealed record PlaybackStateChangedDto(
    string SessionId,
    long Generation,
    string State,
    string Reason);

public sealed record AssignmentChangedDto(IReadOnlyList<AssignmentDto> Assignments);

public sealed record DisplayTopologyChangedDto(IReadOnlyList<DisplayDto> Displays);

public sealed record RendererFaultedDto(
    string SessionId,
    long Generation,
    string ErrorCode,
    string Message,
    bool CircuitBroken);

public sealed record CompatibilityWarningDto(
    string WallpaperId,
    string Grade,
    IReadOnlyList<string> Warnings);
