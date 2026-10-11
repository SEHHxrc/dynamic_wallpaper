using LiveWall.Application.Sessions;
using LiveWall.Application.Policies;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Domain.Playback;
using LiveWall.Domain.Sessions;
using LiveWall.Domain.Wallpapers;
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

internal sealed record DisplayReconciliationPreparedCommand(
    long Generation,
    PreparedApply Prepared,
    IReadOnlyList<SessionId> RetiredSessionIds,
    int ReplacementAttempt = 0) : HostCommand;

internal sealed record DisplayReconciliationFailedCommand(
    long Generation,
    long TopologyRevision,
    string ErrorCode,
    string Message,
    ApplyGenerationPhase Phase,
    ApplyFailureReason FailureReason) : HostCommand;

internal sealed record SystemStateChangedCommand(SystemStateChange Change) : HostCommand;

internal sealed record SessionEnvironmentFailedCommand(
    long SystemRevision, SessionEnvironmentFailureReason Reason) : HostCommand;

internal sealed record SessionEnvironmentCompletedCommand(
    long SystemRevision, SessionEnvironmentPath Path, bool Succeeded,
    SessionEnvironmentFailureReason Reason = SessionEnvironmentFailureReason.None) : HostCommand;

internal sealed record ContinueSessionReconnectCommand(long SystemRevision) : HostCommand;

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
    string Message,
    ApplyGenerationPhase Phase,
    ApplyFailureReason FailureReason,
    ApplyGenerationTerminalState TerminalState = ApplyGenerationTerminalState.Failed)
    : HostCommand;

internal sealed record ExplorerRestartedCommand : HostCommand;

internal sealed record DesktopRecoveryPreparedCommand(
    long Generation,
    PreparedRecovery Recovery) : HostCommand;

internal sealed record DesktopRecoveryFailedCommand(
    long Generation,
    string ErrorCode,
    string Message,
    DesktopRecoveryPhase Phase,
    ApplyFailureReason FailureReason) : HostCommand;

internal sealed record RetryDesktopRecoveryCommand(
    long ExpectedGeneration,
    int Attempt) : HostCommand;

internal sealed record DesktopRecoveryRebuildPreparedCommand(
    long Generation,
    int Attempt,
    PreparedApply Prepared) : HostCommand;

internal sealed record DesktopRecoveryRebuildFailedCommand(
    long Generation,
    int Attempt,
    string ErrorCode,
    string Message,
    ApplyGenerationPhase Phase,
    ApplyFailureReason FailureReason,
    ApplyGenerationTerminalState TerminalState = ApplyGenerationTerminalState.Failed)
    : HostCommand;

internal sealed record GetHostStateCommand : HostCommand;

internal sealed record ShutdownCommand : HostCommand;
