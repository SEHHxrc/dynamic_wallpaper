using LiveWall.Application.Configuration;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Host.CommandLoop;

namespace LiveWall.Host.Bootstrap;

internal sealed class HostBootstrapper
{
    private readonly IHostConfigurationStore configurationStore;

    public HostBootstrapper(IHostConfigurationStore configurationStore)
    {
        this.configurationStore = configurationStore ??
            throw new ArgumentNullException(nameof(configurationStore));
    }

    public async Task<HostBootstrapResult> InitializeAsync(
        HostCommandLoop commandLoop,
        DisplayTopology topology,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commandLoop);
        ArgumentNullException.ThrowIfNull(topology);

        HostConfigurationLoadResult loaded = await configurationStore.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        await commandLoop.SendAsync(
                new SetPlaybackPolicyCommand(loaded.Configuration.PlaybackPolicy),
                cancellationToken)
            .ConfigureAwait(false);
        await commandLoop.SendAsync(new DisplayTopologyChangedCommand(topology), cancellationToken)
            .ConfigureAwait(false);

        HashSet<DisplayId> connectedDisplays = topology.Displays
            .Select(display => display.Id)
            .ToHashSet();
        WallpaperAssignment[] connectedAssignments = loaded.Configuration.Assignments
            .Where(assignment => connectedDisplays.Contains(assignment.DisplayId))
            .ToArray();

        HostCommandResult? restoreResult = null;
        if (connectedAssignments.Length > 0)
        {
            restoreResult = await commandLoop.SendAsync(
                    new RestoreAssignmentsCommand(connectedAssignments),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new HostBootstrapResult(loaded, connectedAssignments, restoreResult);
    }

    public async Task PersistAsync(
        HostStateSnapshot state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        await configurationStore.SavePlaybackPolicyAsync(state.PlaybackPolicy, cancellationToken)
            .ConfigureAwait(false);
        await configurationStore.SaveAssignmentsAsync(state.Assignments, cancellationToken)
            .ConfigureAwait(false);
    }
}

internal sealed record HostBootstrapResult(
    HostConfigurationLoadResult Configuration,
    IReadOnlyList<WallpaperAssignment> ConnectedAssignments,
    HostCommandResult? RestoreResult);
