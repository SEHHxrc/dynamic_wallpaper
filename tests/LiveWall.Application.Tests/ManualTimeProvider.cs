namespace LiveWall.Application.Tests;

/// <summary>A clock that only moves when a test advances it; timers fire on <see cref="Advance"/>.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock gate = new();
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    public int PendingTimerCount
    {
        get
        {
            lock (gate)
            {
                return timers.Count(timer => timer.DueAt is not null);
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer = new(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan duration)
    {
        ManualTimer[] due;
        lock (gate)
        {
            now += duration;
            due = timers.Where(timer => timer.DueAt <= now).ToArray();
            foreach (ManualTimer timer in due)
            {
                timer.DueAt = null; // One-shot; the Host never uses periodic timers.
            }
        }

        foreach (ManualTimer timer in due)
        {
            timer.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAt { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
                if (!owner.timers.Contains(this))
                {
                    owner.timers.Add(this);
                }
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner.gate)
            {
                owner.timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
