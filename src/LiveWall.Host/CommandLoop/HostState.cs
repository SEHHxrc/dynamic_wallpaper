using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Domain.Playback;
using LiveWall.Domain.Sessions;
using LiveWall.Host.Orchestration;

namespace LiveWall.Host.CommandLoop;

internal sealed class HostState
{
    private readonly Dictionary<DisplayId, WallpaperAssignment> assignments = [];
    private readonly Dictionary<SessionId, ActiveHostSession> sessions = [];

    public long Revision { get; private set; }

    public long Generation { get; private set; }

    public DisplayTopology? DisplayTopology { get; private set; }

    public SystemStateSnapshot SystemState { get; private set; } =
        new(false, false, false, false, false, false);

    public UserPlaybackPolicy PlaybackPolicy { get; private set; } = UserPlaybackPolicy.Default;

    public bool ShutdownRequested { get; private set; }

    public long BeginGeneration()
    {
        Generation++;
        Revision++;
        return Generation;
    }

    public void SetDisplayTopology(DisplayTopology topology)
    {
        DisplayTopology = topology;
        Revision++;
    }

    public void SetSystemState(SystemStateSnapshot systemState)
    {
        SystemState = systemState;
        Revision++;
    }

    public void SetPlaybackPolicy(UserPlaybackPolicy policy)
    {
        PlaybackPolicy = policy;
        Revision++;
    }

    public IReadOnlyList<ActiveHostSession> ReplacePreparedSessions(PreparedApply prepared)
    {
        HashSet<DisplayId> replacedDisplays = prepared.Assignments
            .Select(assignment => assignment.DisplayId)
            .ToHashSet();
        ActiveHostSession[] retired = sessions.Values
            .Where(session => session.Prepared.Session.Displays.Any(replacedDisplays.Contains))
            .ToArray();

        foreach (ActiveHostSession session in retired)
        {
            sessions.Remove(session.Prepared.Session.Id);
        }

        foreach (DisplayId displayId in replacedDisplays)
        {
            assignments.Remove(displayId);
        }

        foreach (WallpaperAssignment assignment in prepared.Assignments)
        {
            assignments.Add(assignment.DisplayId, assignment);
        }

        foreach (PreparedSession session in prepared.Sessions)
        {
            sessions.Add(
                session.Session.Id,
                new ActiveHostSession(session, UserPlaybackIntent.Play));
        }

        Revision++;
        return retired;
    }

    public IReadOnlyList<ActiveHostSession> FindSessionsReplacedBy(
        IReadOnlyList<WallpaperAssignment> replacementAssignments)
    {
        HashSet<DisplayId> replacedDisplays = replacementAssignments
            .Select(assignment => assignment.DisplayId)
            .ToHashSet();
        return sessions.Values
            .Where(session => session.Prepared.Session.Displays.Any(replacedDisplays.Contains))
            .ToArray();
    }

    public IReadOnlyList<ActiveHostSession> ChangePlaybackIntent(
        UserPlaybackIntent intent,
        SessionId? sessionId)
    {
        ActiveHostSession[] targets = sessions.Values
            .Where(session => sessionId is null || session.Prepared.Session.Id == sessionId.Value)
            .ToArray();

        foreach (ActiveHostSession target in targets)
        {
            sessions[target.Prepared.Session.Id] = target with { UserIntent = intent };
        }

        if (targets.Length > 0)
        {
            Revision++;
        }

        return targets
            .Select(target => sessions[target.Prepared.Session.Id])
            .ToArray();
    }

    public bool UpdateSession(SessionId sessionId, long generation, PlaybackState state)
    {
        if (!sessions.TryGetValue(sessionId, out ActiveHostSession? active) ||
            active.Prepared.Session.Generation != generation)
        {
            return false;
        }

        sessions[sessionId] = active with
        {
            Prepared = active.Prepared with
            {
                Session = active.Prepared.Session with { State = state },
            },
        };
        Revision++;
        return true;
    }

    public PreparedSession? FindPreparedSession(SessionId sessionId) =>
        sessions.TryGetValue(sessionId, out ActiveHostSession? active)
            ? active.Prepared
            : null;

    public IReadOnlyList<PreparedSession> GetPreparedSessions() =>
        sessions.Values.Select(session => session.Prepared).ToArray();

    public IReadOnlyList<WallpaperAssignment> GetAssignments() =>
        assignments.Values
            .OrderBy(assignment => assignment.DisplayId.Value, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<PreparedSession> InvalidateAllSessions()
    {
        PreparedSession[] invalidated = GetPreparedSessions().ToArray();
        sessions.Clear();
        if (invalidated.Length > 0)
        {
            Revision++;
        }

        return invalidated;
    }

    public bool ReplaceRecoveredSessions(PreparedRecovery recovery)
    {
        if (!CanReplaceRecoveredSessions(recovery))
        {
            return false;
        }

        foreach (RecoveredSession recovered in recovery.Sessions)
        {
            ActiveHostSession active = sessions[recovered.Previous.Session.Id];
            sessions[recovered.Previous.Session.Id] = active with
            {
                Prepared = recovered.Replacement,
            };
        }

        if (recovery.Sessions.Count > 0)
        {
            Revision++;
        }

        return true;
    }

    public bool CanReplaceRecoveredSessions(PreparedRecovery recovery) =>
        recovery.Sessions.All(recovered =>
            sessions.TryGetValue(recovered.Previous.Session.Id, out ActiveHostSession? active) &&
            active.Prepared == recovered.Previous);

    public void RequestShutdown()
    {
        ShutdownRequested = true;
        Revision++;
    }

    public HostStateSnapshot Snapshot() =>
        new(
            Revision,
            Generation,
            ShutdownRequested,
            PlaybackPolicy,
            DisplayTopology,
            assignments.Values
                .OrderBy(assignment => assignment.DisplayId.Value, StringComparer.Ordinal)
                .ToArray(),
            sessions.Values
                .OrderBy(session => session.Prepared.Session.Id.Value, StringComparer.Ordinal)
                .Select(session => new HostSessionSnapshot(
                    session.Prepared.Session,
                    session.UserIntent))
                .ToArray());
}

internal sealed record ActiveHostSession(
    PreparedSession Prepared,
    UserPlaybackIntent UserIntent);

internal sealed record HostStateSnapshot(
    long Revision,
    long Generation,
    bool ShutdownRequested,
    UserPlaybackPolicy PlaybackPolicy,
    DisplayTopology? DisplayTopology,
    IReadOnlyList<WallpaperAssignment> Assignments,
    IReadOnlyList<HostSessionSnapshot> Sessions);

internal sealed record HostSessionSnapshot(
    WallpaperSession Session,
    UserPlaybackIntent UserIntent);
