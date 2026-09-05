using System.Text.Json;
using LiveWall.Contracts.Dtos;

namespace LiveWall.Contracts.Rpc;

public sealed record RpcRequest(
    int ProtocolVersion,
    string RequestId,
    string Method,
    JsonElement Parameters);

public sealed record RpcResponse(
    int ProtocolVersion,
    string RequestId,
    bool Success,
    JsonElement? Result,
    RpcErrorDto? Error);

public sealed record RpcEvent(
    int ProtocolVersion,
    string EventId,
    long StateRevision,
    string Type,
    JsonElement Payload);

public static class UiRpcMethods
{
    public const string GetAppState = nameof(GetAppState);
    public const string ListWallpapers = nameof(ListWallpapers);
    public const string ProbeImport = nameof(ProbeImport);
    public const string ImportWallpaper = nameof(ImportWallpaper);
    public const string CancelImport = nameof(CancelImport);
    public const string ApplyWallpaper = nameof(ApplyWallpaper);
    public const string RemoveWallpaper = nameof(RemoveWallpaper);
    public const string SetWallpaperProperty = nameof(SetWallpaperProperty);
    public const string SetAssignment = nameof(SetAssignment);
    public const string SetPlaybackPolicy = nameof(SetPlaybackPolicy);
    public const string Play = nameof(Play);
    public const string Pause = nameof(Pause);
    public const string Stop = nameof(Stop);
    public const string GetDiagnostics = nameof(GetDiagnostics);
    public const string OpenLogFolder = nameof(OpenLogFolder);
    public const string ShutdownHost = nameof(ShutdownHost);
}

public static class UiEventTypes
{
    public const string AppStateChanged = nameof(AppStateChanged);
    public const string LibraryChanged = nameof(LibraryChanged);
    public const string ImportProgressChanged = nameof(ImportProgressChanged);
    public const string AssignmentChanged = nameof(AssignmentChanged);
    public const string PlaybackStateChanged = nameof(PlaybackStateChanged);
    public const string DisplayTopologyChanged = nameof(DisplayTopologyChanged);
    public const string RendererFaulted = nameof(RendererFaulted);
    public const string CompatibilityWarningRaised = nameof(CompatibilityWarningRaised);
}
