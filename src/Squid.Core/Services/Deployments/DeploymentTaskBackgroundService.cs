using Microsoft.Extensions.Logging;

namespace Squid.Core.Services.Deployments;

public class DeploymentTaskBackgroundService
{
    private readonly IServerTaskRepository _taskRepo;
    private readonly ILogger<DeploymentTaskBackgroundService> _logger;

    public DeploymentTaskBackgroundService(
        IServerTaskRepository taskRepo,
        ILogger<DeploymentTaskBackgroundService> logger)
    {
        _taskRepo = taskRepo;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var task = await _taskRepo.GetPendingTaskAsync();

            if (task != null)
            {
                _logger.LogInformation($"Start processing task {task.Id}");

                await _taskRepo.UpdateStateAsync(task.Id, "Running");

                try
                {
                    // TODO: 调用后续部署计划生成、步骤编排等核心逻辑
                    await Task.Delay(1000, stoppingToken);

                    await _taskRepo.UpdateStateAsync(task.Id, "Success");

                    _logger.LogInformation($"Task {task.Id} completed successfully");
                }
                catch (Exception ex)
                {
                    await _taskRepo.UpdateStateAsync(task.Id, "Failed");

                    _logger.LogError(ex, $"Task {task.Id} failed");
                }
            }
            else
            {
                await Task.Delay(1000, stoppingToken);
            }
        }
    }
}
