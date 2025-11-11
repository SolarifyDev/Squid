using Squid.Core.Services.Deployments.Deployment;
using Squid.Core.Services.Deployments.DeploymentCompletion;
using Squid.Message.Domain.Deployments;
using System.Linq;
using Squid.Core.Services.Common;
using Squid.Core.Services.Deployments.ServerTask;

namespace Squid.Core.Services.Deployments;

public class DeploymentTaskBackgroundService
{
    private readonly IDeploymentPlanService _planService;
    private readonly IDeploymentTargetFinder _targetFinder;
    private readonly IActionCommandGenerator _commandGenerator;
    private readonly IGenericDataProvider _genericDataProvider;
    private readonly IDeploymentVariableResolver _variableResolver;
    private readonly IServerTaskDataProvider _serverTaskDataProvider;
    private readonly IDeploymentDataProvider _deploymentDataProvider;
    private readonly ICommandExecutionService _commandExecutionService;
    private readonly IDeploymentCompletionDataProvider _deploymentCompletionDataProvider;

    public DeploymentTaskBackgroundService(
        IDeploymentPlanService planService,
        IDeploymentVariableResolver variableResolver,
        IDeploymentTargetFinder targetFinder,
        IActionCommandGenerator commandGenerator,
        IDeploymentDataProvider deploymentDataProvider,
        IDeploymentCompletionDataProvider deploymentCompletionDataProvider,
        ICommandExecutionService commandExecutionService, 
        IGenericDataProvider genericDataProvider,
        IServerTaskDataProvider serverTaskDataProvider)
    {
        _planService = planService;
        _variableResolver = variableResolver;
        _targetFinder = targetFinder;
        _commandGenerator = commandGenerator;
        _deploymentDataProvider = deploymentDataProvider;
        _deploymentCompletionDataProvider = deploymentCompletionDataProvider;
        _commandExecutionService = commandExecutionService;
        _genericDataProvider = genericDataProvider;
        _serverTaskDataProvider = serverTaskDataProvider;
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var task = await _serverTaskDataProvider.GetAndLockPendingTaskAsync(stoppingToken).ConfigureAwait(false);

            if (task != null)
            {
                Log.Information("Start processing task {TaskId}", task.Id);

                try
                {
                    await ProcessDeploymentTaskAsync(task, stoppingToken).ConfigureAwait(false);

                    await _genericDataProvider.ExecuteInTransactionAsync(async (cancellationToken) =>
                    {
                        await _serverTaskDataProvider.UpdateServerTaskStateAsync(
                            task.Id, "Success", cancellationToken: cancellationToken).ConfigureAwait(false);
                    }, stoppingToken).ConfigureAwait(false);

                    Log.Information("Task {TaskId} completed successfully", task.Id);
                }
                catch (Exception ex)
                {
                    await _genericDataProvider.ExecuteInTransactionAsync(
                        async (cancellationToken) =>
                        {
                            await _serverTaskDataProvider.UpdateServerTaskStateAsync(task.Id, "Failed", cancellationToken: cancellationToken).ConfigureAwait(false);
                        }, stoppingToken).ConfigureAwait(false);

                    Log.Error(ex, "Task {TaskId} failed: {ErrorMessage}", task.Id, ex.Message);
                }
            }
            else
            {
                await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessDeploymentTaskAsync(Message.Domain.Deployments.ServerTask task, CancellationToken cancellationToken)
    {
        // 通过TaskId查找对应的Deployment
        var deployment = await _deploymentDataProvider.GetDeploymentByTaskIdAsync(task.Id, cancellationToken);

        if (deployment == null) throw new InvalidOperationException($"No deployment found for task {task.Id}");

        // 1. 生成部署计划（包含流程快照）
        Log.Information("Generating deployment plan for deployment {DeploymentId}", deployment.Id);
        var plan = await _planService.GeneratePlanAsync(deployment.Id);

        // 2. 解析变量（包含变量快照）
        Log.Information("Resolving variables for deployment {DeploymentId}", deployment.Id);
        var variables = await _variableResolver.ResolveVariablesAsync(deployment.Id);

        // 3. 筛选目标机器
        Log.Information("Finding targets for deployment {DeploymentId}", deployment.Id);
        var targets = await _targetFinder.FindTargetsAsync(deployment.Id);

        if (!targets.Any()) throw new InvalidOperationException($"No target machines found for deployment {deployment.Id}");

        Log.Information("Found {TargetCount} target machines for deployment {DeploymentId}", targets.Count, deployment.Id);

        // 4. 转换ProcessSnapshot为DeploymentStepDto列表
        var steps = ConvertProcessSnapshotToSteps(plan.ProcessSnapshot);

        // 5. 生成执行命令
        Log.Information("Generating commands for deployment {DeploymentId}", deployment.Id);
        var commands = await _commandGenerator.GenerateCommandsAsync(deployment.Id, targets, steps);

        Log.Information("Generated {CommandCount} commands for deployment {DeploymentId}", commands.Count, deployment.Id);

        // 6. 执行命令
        var executionSuccess = await ExecuteCommandsAsync(deployment.Id, commands, targets, cancellationToken).ConfigureAwait(false);

        // 7. 记录部署完成状态
        await RecordDeploymentCompletionAsync(deployment.Id, executionSuccess,
            executionSuccess ? "Deployment completed successfully" : "Deployment completed with errors").ConfigureAwait(false);

        Log.Information("Deployment {DeploymentId} completed successfully", deployment.Id);
    }

    private List<Squid.Message.Models.Deployments.Process.DeploymentStepDto> ConvertProcessSnapshotToSteps(
        Squid.Message.Models.Deployments.Process.ProcessSnapshotData processSnapshot)
    {
        var steps = new List<Squid.Message.Models.Deployments.Process.DeploymentStepDto>();

        foreach (var stepSnap in processSnapshot.StepSnapshots.OrderBy(p => p.StepOrder))
        {
            var step = new Squid.Message.Models.Deployments.Process.DeploymentStepDto
            {
                Id = stepSnap.Id,
                ProcessId = processSnapshot.Id,
                StepOrder = stepSnap.StepOrder,
                Name = stepSnap.Name,
                StepType = stepSnap.StepType,
                Condition = stepSnap.Condition,
                StartTrigger = "",
                PackageRequirement = "",
                IsDisabled = false,
                IsRequired = true,
                CreatedAt = stepSnap.CreatedAt,
                Properties = stepSnap.Properties.Select(kvp =>
                    new Squid.Message.Models.Deployments.Process.DeploymentStepPropertyDto
                    {
                        Id = 0,
                        StepId = stepSnap.Id,
                        PropertyName = kvp.Key,
                        PropertyValue = kvp.Value
                    }).ToList(),
                Actions = stepSnap.Actions.Select(action =>
                    new Squid.Message.Models.Deployments.Process.DeploymentActionDto
                    {
                        Id = action.Id,
                        StepId = stepSnap.Id,
                        ActionOrder = action.ActionOrder,
                        Name = action.Name,
                        ActionType = action.ActionType,
                        WorkerPoolId = action.WorkerPoolId,
                        IsDisabled = action.IsDisabled,
                        IsRequired = action.IsRequired,
                        CanBeUsedForProjectVersioning = action.CanBeUsedForProjectVersioning,
                        CreatedAt = action.CreatedAt,
                        Properties = action.Properties.Select(kvp =>
                            new Squid.Message.Models.Deployments.Process.DeploymentActionPropertyDto
                            {
                                Id = 0,
                                ActionId = action.Id,
                                PropertyName = kvp.Key,
                                PropertyValue = kvp.Value
                            }).ToList(),
                        Environments = action.Environments,
                        Channels = action.Channels,
                        MachineRoles = action.MachineRoles
                    }).ToList()
            };

            steps.Add(step);
        }

        return steps;
    }

    private async Task<bool> ExecuteCommandsAsync(int deploymentId, List<ActionCommand> commands, List<Squid.Message.Domain.Deployments.Machine> targets, CancellationToken cancellationToken)
    {
        Log.Information("Executing {CommandCount} commands for deployment {DeploymentId}", commands.Count, deploymentId);

        var machineMap = targets.ToDictionary(m => m.Id, m => m);
        var overallSuccess = true;
        var failedCommands = 0;

        foreach (var command in commands)
        {
            if (!machineMap.TryGetValue(command.MachineId, out var targetMachine))
            {
                Log.Error("Target machine {MachineId} not found for command {CommandText}", command.MachineId, command.CommandText);
                overallSuccess = false;
                failedCommands++;
                continue;
            }

            try
            {
                var result = await _commandExecutionService.ExecuteCommandAsync(command, targetMachine, cancellationToken).ConfigureAwait(false);

                if (result.Success)
                {
                    Log.Information("Command completed successfully: {CommandText} on machine {MachineName}. Duration: {Duration}ms",
                        command.CommandText, targetMachine.Name, result.Duration.TotalMilliseconds);

                    if (!string.IsNullOrEmpty(result.Output))
                    {
                        Log.Debug("Command output: {Output}", result.Output);
                    }
                }
                else
                {
                    Log.Error("Command failed: {CommandText} on machine {MachineName}. Error: {Error}",
                        command.CommandText, targetMachine.Name, result.Error);

                    overallSuccess = false;
                    failedCommands++;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Exception occurred while executing command {CommandText} on machine {MachineName}",
                    command.CommandText, targetMachine.Name);

                overallSuccess = false;
                failedCommands++;
            }
        }

        if (overallSuccess)
        {
            Log.Information("All {CommandCount} commands executed successfully for deployment {DeploymentId}", commands.Count, deploymentId);
        }
        else
        {
            Log.Warning("{FailedCount} out of {TotalCount} commands failed for deployment {DeploymentId}",
                failedCommands, commands.Count, deploymentId);
        }

        return overallSuccess;
    }

    private async Task RecordDeploymentCompletionAsync(int deploymentId, bool success, string message)
    {
        // 获取部署信息以获取SpaceId和ReleaseId
        var deployment = await _deploymentDataProvider.GetDeploymentByIdAsync(deploymentId).ConfigureAwait(false);

        var completion = new Message.Domain.Deployments.DeploymentCompletion
        {
            DeploymentId = deploymentId,
            ReleaseId = deployment?.ReleaseId,
            CompletedTime = DateTimeOffset.UtcNow,
            State = success ? "Success" : "Failed",
            SpaceId = deployment?.SpaceId ?? 1, // 如果找不到部署，使用默认SpaceId
            SequenceNumber = 0 // 这个字段会由数据库自动生成
        };

        await _deploymentCompletionDataProvider.AddDeploymentCompletionAsync(completion).ConfigureAwait(false);

        Log.Information("Recorded deployment completion for deployment {DeploymentId} (Release {ReleaseId}): {Status}",
            deploymentId, deployment?.ReleaseId, success ? "Success" : "Failed");
    }
}
