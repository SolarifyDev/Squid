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

    public static long ActiveScripts => Interlocked.Read(ref _activeScripts);
    public static long ScriptsStartedTotal => Interlocked.Read(ref _scriptsStartedTotal);
    public static long ScriptsCompletedTotal => Interlocked.Read(ref _scriptsCompletedTotal);
    public static long ScriptsFailedTotal => Interlocked.Read(ref _scriptsFailedTotal);
    public static long ScriptsCanceledTotal => Interlocked.Read(ref _scriptsCanceledTotal);
    public static long ManagedPods => Interlocked.Read(ref _managedPods);
    public static long OrphanedPodsCleanedTotal => Interlocked.Read(ref _orphanedPodsCleanedTotal);
    public static long NfsForceKillsTotal => Interlocked.Read(ref _nfsForceKillsTotal);

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
    }
}
