using System;
using System.Threading;
using System.Threading.Tasks;
using Squid.Core.Infrastructure.Domain.Deployments;
using Squid.Core.Persistence.Data;
using Squid.Core.Logging;

namespace Squid.Core.Services.Deployments
{
    /// <summary>
    /// 后台任务调度服务，定时拉取待执行 ServerTask 并调度执行。
    /// </summary>
    public class DeploymentTaskBackgroundService
    {
        private readonly IServerTaskRepository _taskRepo;
        private readonly DeploymentOrchestrationService _orchestration;
        private readonly ILogger _logger;

        public DeploymentTaskBackgroundService(
            IServerTaskRepository taskRepo,
            DeploymentOrchestrationService orchestration,
            ILogger logger)
        {
            _taskRepo = taskRepo;
            _orchestration = orchestration;
            _logger = logger;
        }

        /// <summary>
        /// 启动后台任务调度循环。
        /// </summary>
        public async Task RunAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var task = await _taskRepo.GetPendingTaskAsync();
                if (task != null)
                {
                    _logger.Info($"[BackgroundService] 执行 ServerTask: {task.Id}");

                    // 构造 DeploymentTask 并调度
                    var deploymentTask = new DeploymentTask
                    {
                        TaskId = task.Id.ToString(),
                        DeploymentId = task.DeploymentId,
                        Status = DeploymentTaskStatus.Pending
                    };

                    await _orchestration.ExecuteDeploymentTaskAsync(deploymentTask, stoppingToken);
                    await _taskRepo.UpdateStatusAsync(task.Id, deploymentTask.Status.ToString());
                }
                else
                {
                    await Task.Delay(1000, stoppingToken);
                }
            }
        }
    }
}
