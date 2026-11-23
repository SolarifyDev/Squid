namespace Squid.Core.Halibut.RedisQueue;

/// <summary>
/// Says, at most once a minute for the whole server, that requests are being queued locally because the Redis path cannot be used. A Redis outage can last
/// hours with every request for an offline agent falling back, so one line per request would bury everything else in the log.
/// </summary>
public sealed class RedisFallbackLog(TimeProvider clock)
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private long _lastWarned;
    private bool _everWarned;
    private int _suppressed;

    public void Warn(Uri endpoint, string reason)
    {
        int suppressed;

        lock (_gate)
        {
            if (_everWarned && clock.GetElapsedTime(_lastWarned) < Interval)
            {
                _suppressed++;

                return;
            }

            _everWarned = true;
            _lastWarned = clock.GetTimestamp();
            suppressed = _suppressed;
            _suppressed = 0;
        }

        Log.Warning("Queued a request for agent {Endpoint} locally instead of through Redis: {Reason}. It is delivered if the agent connects to this server pod within the polling-queue timeout; {Suppressed} similar fallbacks were not logged in the last minute", endpoint, reason, suppressed);
    }
}
