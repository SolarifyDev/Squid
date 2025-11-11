using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Squid.Core.Services.Deployments;

public class DeploymentTaskHostedService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private Task _executingTask;
    private CancellationTokenSource _stoppingCts;

    public DeploymentTaskHostedService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Log.Information("DeploymentTaskHostedService starting...");

        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _executingTask = ExecuteAsync(_stoppingCts.Token);

        if (_executingTask.IsCompleted)
        {
            return _executingTask;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Log.Information("DeploymentTaskHostedService is stopping...");

        if (_executingTask == null)
        {
            return;
        }

        try
        {
            await _stoppingCts.CancelAsync();
        }
        finally
        {
            await Task.WhenAny(_executingTask, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        }
    }

    private async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var deploymentTaskService = scope.ServiceProvider.GetRequiredService<DeploymentTaskBackgroundService>();

            await deploymentTaskService.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "DeploymentTaskHostedService encountered a fatal error");
            throw;
        }
        finally
        {
            Log.Information("DeploymentTaskHostedService stopped");
        }
    }
}
