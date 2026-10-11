using LiveWall.Domain.Displays;
using LiveWall.Host.CommandLoop;

namespace LiveWall.Host.Bootstrap;

internal sealed record DisplayTopologyForwardingFailure(
    DisplayTopology Topology,
    DateTimeOffset OccurredAt,
    string Reason);

internal sealed class DisplayTopologyEventForwarder
{
    private readonly HostCommandLoop commandLoop;
    private readonly object gate = new();
    private DisplayTopologyForwardingFailure? lastFailure;

    public DisplayTopologyEventForwarder(HostCommandLoop commandLoop)
    {
        this.commandLoop = commandLoop ?? throw new ArgumentNullException(nameof(commandLoop));
    }

    public DisplayTopologyForwardingFailure? LastFailure
    {
        get
        {
            lock (gate)
            {
                return lastFailure;
            }
        }
    }

    public event EventHandler<DisplayTopologyForwardingFailure>? ForwardingFailed;

    public bool TryForward(DisplayTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        if (commandLoop.TryPost(new DisplayTopologyChangedCommand(topology)))
        {
            return true;
        }

        DisplayTopologyForwardingFailure failure = new(
            topology,
            DateTimeOffset.UtcNow,
            "Host command channel is closed; display topology revision was not delivered.");
        lock (gate)
        {
            lastFailure = failure;
        }
        ForwardingFailed?.Invoke(this, failure);
        return false;
    }
}
