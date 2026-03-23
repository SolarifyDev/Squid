using System.Diagnostics;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;
using Serilog;

namespace Squid.Tentacle.Health;

public class KubernetesApiHealthProbe
{
    private readonly IKubernetesPodOperations _ops;
    private readonly KubernetesSettings _settings;
    private volatile bool _isHealthy = true;
    private int _consecutiveFailures;
    private const int UnhealthyThreshold = 3;
    private long _lastLatencyMs;

    public KubernetesApiHealthProbe(IKubernetesPodOperations ops, KubernetesSettings settings)
    {
        _ops = ops;
        _settings = settings;
    }

    public bool IsHealthy => _isHealthy;
    public long LastLatencyMs => _lastLatencyMs;

    public void Check()
    {
        var sw = Stopwatch.StartNew();

        try
        {
            _ops.NamespaceExists(_settings.TentacleNamespace);
            sw.Stop();

            _lastLatencyMs = sw.ElapsedMilliseconds;
            _consecutiveFailures = 0;
            _isHealthy = true;

            TentacleMetrics.RecordApiLatency(_lastLatencyMs);

            if (_lastLatencyMs > 5000)
                Log.Warning("Kubernetes API latency is high: {LatencyMs}ms", _lastLatencyMs);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _lastLatencyMs = sw.ElapsedMilliseconds;
            _consecutiveFailures++;
            _isHealthy = _consecutiveFailures < UnhealthyThreshold;

            Log.Warning(ex, "Kubernetes API health check failed ({Consecutive}/{Threshold}) for namespace {Namespace}, latency={LatencyMs}ms",
                _consecutiveFailures, UnhealthyThreshold, _settings.TentacleNamespace, _lastLatencyMs);
        }
    }
}
