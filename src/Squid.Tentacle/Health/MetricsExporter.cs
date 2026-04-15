using System.Text;

namespace Squid.Tentacle.Health;

/// <summary>
/// Exports TentacleMetrics in Prometheus text exposition format.
/// </summary>
public static class MetricsExporter
{
    public static string ExportPrometheus()
    {
        var sb = new StringBuilder();

        AppendGauge(sb, "squid_tentacle_active_scripts", "Number of currently running scripts", TentacleMetrics.ActiveScripts);
        AppendCounter(sb, "squid_tentacle_scripts_started_total", "Total number of scripts started", TentacleMetrics.ScriptsStartedTotal);
        AppendCounter(sb, "squid_tentacle_scripts_completed_total", "Total number of scripts completed successfully", TentacleMetrics.ScriptsCompletedTotal);
        AppendCounter(sb, "squid_tentacle_scripts_failed_total", "Total number of scripts that failed", TentacleMetrics.ScriptsFailedTotal);
        AppendCounter(sb, "squid_tentacle_scripts_canceled_total", "Total number of scripts canceled", TentacleMetrics.ScriptsCanceledTotal);
        AppendGauge(sb, "squid_tentacle_managed_pods", "Number of managed script pods", TentacleMetrics.ManagedPods);
        AppendCounter(sb, "squid_tentacle_orphaned_pods_cleaned_total", "Total number of orphaned pods cleaned up", TentacleMetrics.OrphanedPodsCleanedTotal);
        AppendCounter(sb, "squid_tentacle_nfs_force_kills_total", "Total number of NFS watchdog force-kill pod deletions", TentacleMetrics.NfsForceKillsTotal);

        // Only emit the cert-expiry gauge once it's been set. Prevents Prometheus
        // from alerting on the sentinel -1 during the brief window between service
        // start and the cert loader publishing the real days-to-expiry.
        if (TentacleMetrics.CertificateExpiresInDays >= 0)
            AppendGauge(sb, "squid_tentacle_certificate_expires_in_days",
                "Days remaining until the Tentacle self-signed certificate expires",
                TentacleMetrics.CertificateExpiresInDays);

        return sb.ToString();
    }

    private static void AppendGauge(StringBuilder sb, string name, string help, long value)
    {
        sb.AppendLine($"# HELP {name} {help}");
        sb.AppendLine($"# TYPE {name} gauge");
        sb.AppendLine($"{name} {value}");
    }

    private static void AppendCounter(StringBuilder sb, string name, string help, long value)
    {
        sb.AppendLine($"# HELP {name} {help}");
        sb.AppendLine($"# TYPE {name} counter");
        sb.AppendLine($"{name} {value}");
    }
}
