using Serilog;
using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Health;

namespace Squid.Tentacle.Kubernetes;

public sealed class NfsWatchdog : ITentacleBackgroundTask
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    private readonly string _workspacePath;
    private readonly IKubernetesPodOperations _podOps;
    private readonly KubernetesSettings _settings;
    private volatile bool _isHealthy = true;
    private int _consecutiveFailures;

    public NfsWatchdog(string workspacePath, IKubernetesPodOperations podOps, KubernetesSettings settings)
    {
        _workspacePath = workspacePath;
        _podOps = podOps;
        _settings = settings;
    }

    public string Name => "NfsWatchdog";

    public bool IsHealthy => _isHealthy;

    public int ConsecutiveFailures => _consecutiveFailures;

    public async Task RunAsync(CancellationToken ct)
    {
        Log.Information("NFS watchdog started. Monitoring workspace path: {Path}", _workspacePath);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                CheckWorkspaceHealth();
                await Task.Delay(CheckInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "NFS watchdog check failed unexpectedly");
            }
        }
    }

    internal void CheckWorkspaceHealth()
    {
        var sentinelPath = Path.Combine(_workspacePath, ".squid-nfs-watchdog");

        try
        {
            File.WriteAllText(sentinelPath, DateTime.UtcNow.ToString("O"));

            var readBack = File.ReadAllText(sentinelPath);

            File.Delete(sentinelPath);

            if (string.IsNullOrEmpty(readBack))
                throw new IOException("Sentinel file read-back was empty");

            _consecutiveFailures = 0;

            if (!_isHealthy)
            {
                _isHealthy = true;
                Log.Information("NFS workspace recovered at {Path}", _workspacePath);
            }
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;

            if (_isHealthy)
            {
                _isHealthy = false;
                Log.Error(ex, "NFS workspace unhealthy at {Path}", _workspacePath);
            }

            if (_consecutiveFailures >= _settings.NfsWatchdogForceKillThreshold)
                ForceDeleteSelf();
        }
    }

    private void ForceDeleteSelf()
    {
        var hostname = Environment.GetEnvironmentVariable("HOSTNAME") ?? System.Net.Dns.GetHostName();

        Log.Fatal("NFS watchdog: {N} consecutive failures, force-deleting agent pod {PodName}", _consecutiveFailures, hostname);

        TentacleMetrics.NfsForceKill();

        try
        {
            _podOps.DeletePod(hostname, _settings.TentacleNamespace, 0);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to force-delete agent pod {PodName}", hostname);
        }
    }
}
