using Serilog;
using Squid.Tentacle.Abstractions;

namespace Squid.Tentacle.Kubernetes;

public sealed class ResilientBackgroundTask : ITentacleBackgroundTask
{
    private readonly ITentacleBackgroundTask _inner;
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    public ResilientBackgroundTask(ITentacleBackgroundTask inner)
    {
        _inner = inner;
    }

    public string Name => _inner.Name;

    public async Task RunAsync(CancellationToken ct)
    {
        var consecutiveFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _inner.RunAsync(ct).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                var backoff = CalculateBackoff(consecutiveFailures);

                Log.Warning(ex, "Background task {TaskName} crashed (failure #{Count}), restarting in {BackoffSeconds}s",
                    _inner.Name, consecutiveFailures, backoff.TotalSeconds);

                try
                {
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    internal static TimeSpan CalculateBackoff(int consecutiveFailures)
    {
        var seconds = Math.Min(Math.Pow(2, consecutiveFailures - 1), MaxBackoff.TotalSeconds);
        return TimeSpan.FromSeconds(Math.Max(seconds, MinBackoff.TotalSeconds));
    }
}
