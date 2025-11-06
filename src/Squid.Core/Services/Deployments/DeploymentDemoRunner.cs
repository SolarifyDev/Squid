using System;
using System.Threading;
using System.Threading.Tasks;
using Squid.Core.Logging;

namespace Squid.Core.Services.Deployments
{
    /// <summary>
    /// 端到端部署主流程演示Runner
    /// </summary>
    public class DeploymentDemoRunner
    {
        public static async Task RunDemoAsync()
        {
            // 1. 构造依赖（实际应用中用DI容器注入）
            var logger = new ConsoleLogger();
            var variableSnapshotService = new MockVariableSnapshotService();
            var processDataProvider = new MockDeploymentProcessDataProvider();
            var planner = new DeploymentPlanner(variableSnapshotService, processDataProvider, logger);
            var orchestration = new DeploymentOrchestrationService(planner, logger);

            // 2. 创建一个部署任务
            var task = new DeploymentTask
            {
                TaskId = Guid.NewGuid().ToString(),
                DeploymentId = 1,
                Status = DeploymentTaskStatus.Pending
            };

            // 3. 入队
            orchestration.EnqueueTask(task);

            // 4. 启动后台调度（本例只跑一次）
            using var cts = new CancellationTokenSource();
            var runTask = orchestration.RunQueueAsync(cts.Token);

            // 5. 等待任务执行完成
            await Task.Delay(3000);
            cts.Cancel();
        }
    }

    // 简单ConsoleLogger实现
    public class ConsoleLogger : ILogger
    {
        public void Info(string message) => Console.WriteLine("[INFO] " + message);
        public void Error(string message) => Console.WriteLine("[ERROR] " + message);
    }

    // Mock实现：变量快照服务
    public class MockVariableSnapshotService : IHybridVariableSnapshotService
    {
        public Task<VariableSetSnapshotData> LoadCompleteVariableSetAsync(int variableSetId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new VariableSetSnapshotData
            {
                Variables = new System.Collections.Generic.List<VariableSnapshotData>
                {
                    new VariableSnapshotData { Name = "DbPassword", Value = "prod_secret", Priority = 1 },
                    new VariableSnapshotData { Name = "DatabasePassword", Value = "#{DbPassword}", Priority = 2 },
                    new VariableSnapshotData { Name = "ApiEndpoint", Value = "https://api.prod.com", Priority = 1 }
                }
            });
        }
    }

    // Mock实现：流程与步骤数据
    public class MockDeploymentProcessDataProvider : IDeploymentProcessDataProvider
    {
        public Task<dynamic> GetDeploymentProcessByDeploymentIdAsync(int deploymentId, CancellationToken cancellationToken)
        {
            // 返回匿名对象模拟流程
            return Task.FromResult((dynamic)new { Id = 100, VariableSetId = 10 });
        }

        public Task<System.Collections.Generic.List<dynamic>> GetDeploymentStepsByProcessIdAsync(int processId, CancellationToken cancellationToken)
        {
            // 返回两个步骤：一个串行、一个并发
            var steps = new System.Collections.Generic.List<dynamic>
            {
                new {
                    StepOrder = 1,
                    Name = "StopService",
                    StepType = "serial",
                    Actions = new System.Collections.Generic.List<dynamic>
                    {
                        new { Name = "StopApp", Target = "ServerA" }
                    }
                },
                new {
                    StepOrder = 2,
                    Name = "DeployPackage",
                    StepType = "parallel",
                    Actions = new System.Collections.Generic.List<dynamic>
                    {
                        new { Name = "DeployToA", Target = "ServerA" },
                        new { Name = "DeployToB", Target = "ServerB" }
                    }
                }
            };
            return Task.FromResult(steps);
        }
    }

    // Mock接口定义
    public interface IHybridVariableSnapshotService
    {
        Task<VariableSetSnapshotData> LoadCompleteVariableSetAsync(int variableSetId, CancellationToken cancellationToken);
    }

    public interface IDeploymentProcessDataProvider
    {
        Task<dynamic> GetDeploymentProcessByDeploymentIdAsync(int deploymentId, CancellationToken cancellationToken);
        Task<System.Collections.Generic.List<dynamic>> GetDeploymentStepsByProcessIdAsync(int processId, CancellationToken cancellationToken);
    }
}
