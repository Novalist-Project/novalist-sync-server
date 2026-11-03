using System.Threading.Channels;

namespace Novalist.Sync.Server.Tests.Relay;

internal sealed class RelayTestClock : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ClockTimer> timers = new();
    private readonly Channel<TimeSpan> scheduled = Channel.CreateUnbounded<TimeSpan>();
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() { lock (gate) return now; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ClockTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public Task<TimeSpan> NextDelayAsync() => scheduled.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
        .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    /// <summary>Runs due timers, including zero-due timers scheduled since the previous advance.</summary>
    public void Advance(TimeSpan duration)
    {
        lock (gate) now += duration;
        while (true)
        {
            ClockTimer? ready;
            lock (gate)
            {
                ready = timers.Where(timer => timer.Due <= now).MinBy(timer => timer.Due);
                if (ready is null) return;
                ready.Due = ready.Period > TimeSpan.Zero ? ready.Due + ready.Period : DateTimeOffset.MaxValue;
            }
            ready.Fire();
        }
    }

    private sealed class ClockTimer(RelayTestClock clock, TimerCallback callback, object? state) : ITimer
    {
        private bool disposed;
        public DateTimeOffset Due { get; set; }
        public TimeSpan Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock.gate)
            {
                if (disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.now + dueTime;
                Period = period;
                if (!clock.timers.Contains(this)) clock.timers.Add(this);
                clock.scheduled.Writer.TryWrite(dueTime);
                return true;
            }
        }

        public void Fire()
        {
            lock (clock.gate) if (disposed) return;
            callback(state);
        }
        public void Dispose() { lock (clock.gate) { disposed = true; clock.timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
