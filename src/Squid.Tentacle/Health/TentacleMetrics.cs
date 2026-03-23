using System.Collections.Concurrent;

namespace Squid.Tentacle.Health;

/// <summary>
/// Lightweight in-process metrics for Tentacle observability.
/// Thread-safe counters and gauges exposed via Prometheus text format.
/// </summary>
public static class TentacleMetrics
{
    private static long _activeScripts;
    private static long _scriptsStartedTotal;
    private static long _scriptsCompletedTotal;
    private static long _scriptsFailedTotal;
    private static long _scriptsCanceledTotal;
    private static long _managedPods;
    private static long _orphanedPodsCleanedTotal;
    private static long _nfsForceKillsTotal;
    private static long _scriptsQueuedTotal;
    private static long _scriptsRejectedTotal;
    private static long _apiLatencyMs;

    public static long ActiveScripts => Interlocked.Read(ref _activeScripts);
    public static long ScriptsStartedTotal => Interlocked.Read(ref _scriptsStartedTotal);
    public static long ScriptsCompletedTotal => Interlocked.Read(ref _scriptsCompletedTotal);
    public static long ScriptsFailedTotal => Interlocked.Read(ref _scriptsFailedTotal);
    public static long ScriptsCanceledTotal => Interlocked.Read(ref _scriptsCanceledTotal);
    public static long ManagedPods => Interlocked.Read(ref _managedPods);
    public static long OrphanedPodsCleanedTotal => Interlocked.Read(ref _orphanedPodsCleanedTotal);
    public static long NfsForceKillsTotal => Interlocked.Read(ref _nfsForceKillsTotal);
    public static long ScriptsQueuedTotal => Interlocked.Read(ref _scriptsQueuedTotal);
    public static long ScriptsRejectedTotal => Interlocked.Read(ref _scriptsRejectedTotal);
    public static long ApiLatencyMs => Interlocked.Read(ref _apiLatencyMs);

    public static void ScriptStarted()
    {
        Interlocked.Increment(ref _activeScripts);
        Interlocked.Increment(ref _scriptsStartedTotal);
    }

    public static void ScriptCompleted()
    {
        Interlocked.Decrement(ref _activeScripts);
        Interlocked.Increment(ref _scriptsCompletedTotal);
    }

    public static void ScriptFailed()
    {
        Interlocked.Decrement(ref _activeScripts);
        Interlocked.Increment(ref _scriptsFailedTotal);
    }

    public static void ScriptCanceled()
    {
        Interlocked.Decrement(ref _activeScripts);
        Interlocked.Increment(ref _scriptsCanceledTotal);
    }

    public static void SetManagedPods(long count)
    {
        Interlocked.Exchange(ref _managedPods, count);
    }

    public static void OrphanedPodCleaned()
    {
        Interlocked.Increment(ref _orphanedPodsCleanedTotal);
    }

    public static void NfsForceKill()
    {
        Interlocked.Increment(ref _nfsForceKillsTotal);
    }

    public static void ScriptQueued()
    {
        Interlocked.Increment(ref _scriptsQueuedTotal);
    }

    public static void ScriptRejected()
    {
        Interlocked.Increment(ref _scriptsRejectedTotal);
    }

    public static void RecordApiLatency(long ms)
    {
        Interlocked.Exchange(ref _apiLatencyMs, ms);
    }

    public static MetricsSnapshot TakeSnapshot()
    {
        return new MetricsSnapshot
        {
            ScriptsStartedTotal = ScriptsStartedTotal,
            ScriptsCompletedTotal = ScriptsCompletedTotal,
            ScriptsFailedTotal = ScriptsFailedTotal,
            ScriptsCanceledTotal = ScriptsCanceledTotal,
            OrphanedPodsCleanedTotal = OrphanedPodsCleanedTotal,
            NfsForceKillsTotal = NfsForceKillsTotal,
            ScriptsQueuedTotal = ScriptsQueuedTotal,
            ScriptsRejectedTotal = ScriptsRejectedTotal
        };
    }

    public static void RestoreFrom(MetricsSnapshot snapshot)
    {
        Interlocked.Exchange(ref _scriptsStartedTotal, snapshot.ScriptsStartedTotal);
        Interlocked.Exchange(ref _scriptsCompletedTotal, snapshot.ScriptsCompletedTotal);
        Interlocked.Exchange(ref _scriptsFailedTotal, snapshot.ScriptsFailedTotal);
        Interlocked.Exchange(ref _scriptsCanceledTotal, snapshot.ScriptsCanceledTotal);
        Interlocked.Exchange(ref _orphanedPodsCleanedTotal, snapshot.OrphanedPodsCleanedTotal);
        Interlocked.Exchange(ref _nfsForceKillsTotal, snapshot.NfsForceKillsTotal);
        Interlocked.Exchange(ref _scriptsQueuedTotal, snapshot.ScriptsQueuedTotal);
        Interlocked.Exchange(ref _scriptsRejectedTotal, snapshot.ScriptsRejectedTotal);
    }

    internal static void Reset()
    {
        Interlocked.Exchange(ref _activeScripts, 0);
        Interlocked.Exchange(ref _scriptsStartedTotal, 0);
        Interlocked.Exchange(ref _scriptsCompletedTotal, 0);
        Interlocked.Exchange(ref _scriptsFailedTotal, 0);
        Interlocked.Exchange(ref _scriptsCanceledTotal, 0);
        Interlocked.Exchange(ref _managedPods, 0);
        Interlocked.Exchange(ref _orphanedPodsCleanedTotal, 0);
        Interlocked.Exchange(ref _nfsForceKillsTotal, 0);
        Interlocked.Exchange(ref _scriptsQueuedTotal, 0);
        Interlocked.Exchange(ref _scriptsRejectedTotal, 0);
        Interlocked.Exchange(ref _apiLatencyMs, 0);
    }
}
