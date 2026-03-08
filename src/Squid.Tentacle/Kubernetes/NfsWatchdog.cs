using Serilog;
using Squid.Tentacle.Abstractions;

namespace Squid.Tentacle.Kubernetes;

public sealed class NfsWatchdog : ITentacleBackgroundTask
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    private readonly string _workspacePath;
    private volatile bool _isHealthy = true;

    public NfsWatchdog(string workspacePath)
    {
        _workspacePath = workspacePath;
    }

    public string Name => "NfsWatchdog";

    public bool IsHealthy => _isHealthy;

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

    private void CheckWorkspaceHealth()
    {
        var sentinelPath = Path.Combine(_workspacePath, ".squid-nfs-watchdog");

        try
        {
            File.WriteAllText(sentinelPath, DateTime.UtcNow.ToString("O"));

            var readBack = File.ReadAllText(sentinelPath);

            File.Delete(sentinelPath);

            if (string.IsNullOrEmpty(readBack))
                throw new IOException("Sentinel file read-back was empty");

            if (!_isHealthy)
            {
                _isHealthy = true;
                Log.Information("NFS workspace recovered at {Path}", _workspacePath);
            }
        }
        catch (Exception ex)
        {
            if (_isHealthy)
            {
                _isHealthy = false;
                Log.Error(ex, "NFS workspace unhealthy at {Path}", _workspacePath);
            }
        }
    }
}
