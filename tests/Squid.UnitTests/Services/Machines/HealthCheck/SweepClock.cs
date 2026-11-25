using System.Collections.Concurrent;
using System.Linq;
using Microsoft.Extensions.Time.Testing;

namespace Squid.UnitTests.Services.Machines.HealthCheck;

/// <summary>
/// A fake clock for the health-check coordinator. Every timer the coordinator arms (sweep deadline, probe timeout, timeout grace) goes through
/// <see cref="CreateTimer"/>, so a test can read the exact duration each one was armed with and wait until a timer exists before moving time past it.
/// </summary>
internal sealed class SweepClock() : FakeTimeProvider(DateTimeOffset.UnixEpoch)
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private readonly ConcurrentQueue<TimeSpan> _armed = new();

    public IReadOnlyList<TimeSpan> Armed => _armed.ToList();

    public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);

        _armed.Enqueue(dueTime);

        return timer;
    }

    /// <summary>Waits until the code under test has armed <paramref name="count"/> timers; fails with a clear message instead of hanging.</summary>
    public async Task WaitForTimersAsync(int count)
    {
        var limit = DateTime.UtcNow + WaitLimit;

        while (_armed.Count < count)
        {
            if (DateTime.UtcNow > limit)
                throw new TimeoutException($"Expected {count} timers to be armed within {WaitLimit.TotalSeconds}s, saw {_armed.Count}: {string.Join(", ", _armed)}");

            await Task.Delay(5);
        }
    }

    /// <summary>Waits until <paramref name="condition"/> holds; fails with <paramref name="what"/> instead of hanging.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var limit = DateTime.UtcNow + WaitLimit;

        while (!condition())
        {
            if (DateTime.UtcNow > limit)
                throw new TimeoutException($"Timed out after {WaitLimit.TotalSeconds}s waiting for: {what}");

            await Task.Delay(5);
        }
    }
}
