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
    private static long _orphanedWorkspacesCleanedTotal;
    private static long _nfsForceKillsTotal;
    private static long _scriptsQueuedTotal;
    private static long _scriptsRejectedTotal;
    private static long _apiLatencyMs;
    private static long _certificateExpiresInDays = -1;

    public static long ActiveScripts => Interlocked.Read(ref _activeScripts);
    public static long ScriptsStartedTotal => Interlocked.Read(ref _scriptsStartedTotal);
    public static long ScriptsCompletedTotal => Interlocked.Read(ref _scriptsCompletedTotal);
    public static long ScriptsFailedTotal => Interlocked.Read(ref _scriptsFailedTotal);
    public static long ScriptsCanceledTotal => Interlocked.Read(ref _scriptsCanceledTotal);
    public static long ManagedPods => Interlocked.Read(ref _managedPods);
    public static long OrphanedPodsCleanedTotal => Interlocked.Read(ref _orphanedPodsCleanedTotal);

    /// <summary>
    /// P1-Phase9.11 — total Tentacle workspace directories swept by the
    /// background cleanup loop (default every 10 min, TTL configurable via
    /// <c>SQUID_TENTACLE_ORPHAN_WORKSPACE_TTL_HOURS</c>). Operators alert
    /// when this rate spikes (script crashes leaving workspaces behind) or
    /// when it stays at 0 over many hours despite active deploys (cleanup
    /// loop deadlocked).
    /// </summary>
    public static long OrphanedWorkspacesCleanedTotal => Interlocked.Read(ref _orphanedWorkspacesCleanedTotal);
    public static long NfsForceKillsTotal => Interlocked.Read(ref _nfsForceKillsTotal);
    public static long ScriptsQueuedTotal => Interlocked.Read(ref _scriptsQueuedTotal);
    public static long ScriptsRejectedTotal => Interlocked.Read(ref _scriptsRejectedTotal);
    public static long ApiLatencyMs => Interlocked.Read(ref _apiLatencyMs);

    /// <summary>
    /// Days remaining until the Tentacle's own certificate expires, or
    /// <c>-1</c> when not yet set (service hasn't called
    /// <see cref="SetCertificateExpiresInDays"/> since start). Exposed as
    /// <c>squid_tentacle_certificate_expires_in_days</c> so operators can
    /// alert in Prometheus well before the 100-year cert ever becomes an
    /// issue (e.g. warn at 180 days, critical at 30 days).
    /// </summary>
    public static long CertificateExpiresInDays => Interlocked.Read(ref _certificateExpiresInDays);

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

    /// <summary>
    /// P1-Phase9.11 — call once per orphaned workspace dir actually deleted
    /// by <c>LocalScriptService.CleanupOrphanedWorkspaces</c>. Skipped dirs
    /// (still in TTL, or in-flight) do NOT count.
    /// </summary>
    public static void OrphanedWorkspaceCleaned()
    {
        Interlocked.Increment(ref _orphanedWorkspacesCleanedTotal);
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

    /// <summary>
    /// Publishes the Tentacle certificate's days-to-expiry so Prometheus can
    /// alert on it. Should be called at startup (when the cert is first
    /// loaded) and periodically thereafter if the cert is ever rotated
    /// without a full service restart.
    /// </summary>
    public static void SetCertificateExpiresInDays(long days)
    {
        Interlocked.Exchange(ref _certificateExpiresInDays, days);
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
        Interlocked.Exchange(ref _certificateExpiresInDays, -1);
    }
}
