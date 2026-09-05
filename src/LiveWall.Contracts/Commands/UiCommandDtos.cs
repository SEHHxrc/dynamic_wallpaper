using System.Text.Json;

namespace LiveWall.Contracts.Commands;

public sealed record EmptyRequestDto;

public sealed record ListWallpapersRequestDto(
    string? SearchText = null,
    string? Kind = null,
    int Offset = 0,
    int Limit = 100);

public sealed record ProbeImportRequestDto(string SourcePath);

public sealed record ImportWallpaperRequestDto(string SourcePath, string? PreferredStrategy);

public sealed record CancelImportRequestDto(string OperationId);

public sealed record ApplyWallpaperRequestDto(
    string WallpaperId,
    IReadOnlyList<string> DisplayIds,
    string LayoutMode,
    string FitMode,
    string? PropertyPresetId);

public sealed record RemoveWallpaperRequestDto(string WallpaperId);

public sealed record SetAssignmentRequestDto(
    string DisplayId,
    string WallpaperId,
    string LayoutMode,
    string FitMode,
    string? PropertyPresetId);

public sealed record SetWallpaperPropertyRequestDto(
    string WallpaperId,
    string PropertyName,
    JsonElement Value,
    string? PropertyPresetId);

public sealed record PlaybackPolicyDto(
    bool PauseWhenDisplayOff,
    bool PauseWhenSessionLocked,
    bool PauseInRemoteSession,
    bool PauseOnBattery,
    bool PauseForFullscreenApplication,
    int DefaultFramesPerSecond,
    int ThrottledFramesPerSecond);

public sealed record PlaybackTargetRequestDto(
    string? SessionId = null,
    IReadOnlyList<string>? DisplayIds = null);
