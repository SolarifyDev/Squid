namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>Timers that fire only when the test ticks them.</summary>
internal sealed class ManualTimers : TimeProvider
{
    private readonly List<(TimerCallback Callback, object State)> _timers = [];
    private readonly TaskCompletionSource _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DateTimeOffset _now = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);

    public List<TimeSpan> Periods { get; } = [];

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_timers) _timers.Add((callback, state));

        Periods.Add(period == Timeout.InfiniteTimeSpan ? dueTime : period);
        _created.TrySetResult();

        return new Timer(this);
    }

    public Task WaitForTimerAsync() => _created.Task.WaitAsync(TimeSpan.FromSeconds(5));

    public void Tick()
    {
        List<(TimerCallback Callback, object State)> timers;

        lock (_timers) timers = [.. _timers];

        foreach (var (callback, state) in timers) callback(state);
    }

    private sealed class Timer(ManualTimers owner) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.Periods.Add(period == Timeout.InfiniteTimeSpan ? dueTime : period);

            return true;
        }

        public void Dispose()
        {
            lock (owner._timers) owner._timers.Clear();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
