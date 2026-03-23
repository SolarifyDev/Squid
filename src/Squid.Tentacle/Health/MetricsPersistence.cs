using System.Text.Json;
using k8s.Models;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;
using Serilog;


namespace Squid.Tentacle.Health;

public class MetricsPersistence
{
    private const string ConfigMapName = "squid-tentacle-metrics";
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(60);

    private readonly IKubernetesPodOperations _ops;
    private readonly KubernetesSettings _settings;
    private DateTime _lastSave = DateTime.MinValue;

    public MetricsPersistence(IKubernetesPodOperations ops, KubernetesSettings settings)
    {
        _ops = ops;
        _settings = settings;
    }

    public void Restore()
    {
        try
        {
            var configMaps = _ops.ListConfigMaps(_settings.TentacleNamespace, $"metadata.name={ConfigMapName}");
            var cm = configMaps?.Items?.FirstOrDefault(c => c.Metadata?.Name == ConfigMapName);

            if (cm?.Data == null || !cm.Data.TryGetValue("metrics", out var json))
            {
                Log.Debug("No metrics ConfigMap found, starting fresh");
                return;
            }

            var snapshot = JsonSerializer.Deserialize<MetricsSnapshot>(json);
            if (snapshot == null) return;

            TentacleMetrics.RestoreFrom(snapshot);
            Log.Information("Restored metrics from ConfigMap: {Started} started, {Completed} completed, {Failed} failed", snapshot.ScriptsStartedTotal, snapshot.ScriptsCompletedTotal, snapshot.ScriptsFailedTotal);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to restore metrics from ConfigMap");
        }
    }

    public void SaveIfDue()
    {
        if (DateTime.UtcNow - _lastSave < SaveInterval) return;

        Save();
    }

    public void Save()
    {
        try
        {
            var snapshot = TentacleMetrics.TakeSnapshot();
            var json = JsonSerializer.Serialize(snapshot);

            var cm = new V1ConfigMap
            {
                Metadata = new V1ObjectMeta
                {
                    Name = ConfigMapName,
                    NamespaceProperty = _settings.TentacleNamespace,
                    Labels = new Dictionary<string, string>
                    {
                        ["app.kubernetes.io/managed-by"] = "kubernetes-agent",
                        ["squid.io/context-type"] = "metrics"
                    }
                },
                Data = new Dictionary<string, string>
                {
                    ["metrics"] = json,
                    ["lastSaved"] = DateTime.UtcNow.ToString("O")
                }
            };

            HelmMetadata.ApplyHelmAnnotations(cm.Metadata, _settings);
            _ops.CreateOrReplaceConfigMap(cm, _settings.TentacleNamespace);
            _lastSave = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to save metrics to ConfigMap");
        }
    }
}

public class MetricsSnapshot
{
    public long ScriptsStartedTotal { get; set; }
    public long ScriptsCompletedTotal { get; set; }
    public long ScriptsFailedTotal { get; set; }
    public long ScriptsCanceledTotal { get; set; }
    public long OrphanedPodsCleanedTotal { get; set; }
    public long NfsForceKillsTotal { get; set; }
    public long ScriptsQueuedTotal { get; set; }
    public long ScriptsRejectedTotal { get; set; }
}
