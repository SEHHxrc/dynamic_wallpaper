using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Domain.Sessions;
using LiveWall.Domain.Wallpapers;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Playback;
using LiveWall.Host.Orchestration;

namespace LiveWall.Host.CommandLoop;

internal abstract record HostCommand;

internal sealed record ApplyWallpaperCommand(
    WallpaperId WallpaperId,
    IReadOnlyList<DisplayId> Displays,
    LayoutMode LayoutMode,
    FitMode FitMode,
    PropertyPresetId? PropertyPresetId = null) : HostCommand;

internal sealed record RestoreAssignmentsCommand(
    IReadOnlyList<WallpaperAssignment> Assignments) : HostCommand;

internal sealed record RemoveWallpaperCommand(WallpaperId WallpaperId) : HostCommand;

internal sealed record DisplayTopologyChangedCommand(DisplayTopology Topology) : HostCommand;

internal sealed record SystemPolicyChangedCommand(SystemStateSnapshot SystemState) : HostCommand;

internal sealed record SetPlaybackPolicyCommand(UserPlaybackPolicy Policy) : HostCommand;

internal sealed record ChangePlaybackIntentCommand(
    UserPlaybackIntent Intent,
    SessionId? SessionId = null) : HostCommand;

internal sealed record RendererEventCommand(
    SessionId SessionId,
    long Generation,
    RendererEvent RendererEvent) : HostCommand;

internal sealed record RendererExitedCommand(SessionId SessionId, long Generation, int ExitCode) : HostCommand;

internal sealed record ApplyPreparedCommand(long Generation, PreparedApply Prepared) : HostCommand;

internal sealed record ApplyPreparationFailedCommand(
    long Generation,
    string ErrorCode,
    string Message) : HostCommand;

internal sealed record ExplorerRestartedCommand : HostCommand;

internal sealed record DesktopRecoveryPreparedCommand(
    long Generation,
    PreparedRecovery Recovery) : HostCommand;

internal sealed record DesktopRecoveryFailedCommand(
    long Generation,
    string ErrorCode,
    string Message) : HostCommand;

internal sealed record GetHostStateCommand : HostCommand;

internal sealed record ShutdownCommand : HostCommand;
