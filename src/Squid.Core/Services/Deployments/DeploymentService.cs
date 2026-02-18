using System.Text.Json;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Environments;
using Squid.Core.Services.Deployments.Exceptions;
using Squid.Core.Services.Deployments.Machine;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Jobs;
using Squid.Message.Commands.Deployments.Deployment;
using Squid.Message.Events.Deployments.Deployment;
using Squid.Message.Models.Deployments.Deployment;

namespace Squid.Core.Services.Deployments;

public class DeploymentService : IDeploymentService
{
    private readonly IMapper _mapper;
    private readonly IDeploymentDataProvider _deploymentDataProvider;
    private readonly IReleaseDataProvider _releaseDataProvider;
    private readonly IEnvironmentDataProvider _environmentDataProvider;
    private readonly IMachineDataProvider _machineDataProvider;
    private readonly IServerTaskDataProvider _serverTaskDataProvider;
    private readonly ISquidBackgroundJobClient _backgroundJobClient;

    public DeploymentService(
        IMapper mapper,
        IDeploymentDataProvider deploymentDataProvider,
        IReleaseDataProvider releaseDataProvider,
        IEnvironmentDataProvider environmentDataProvider,
        IMachineDataProvider machineDataProvider,
        IServerTaskDataProvider serverTaskDataProvider,
        ISquidBackgroundJobClient backgroundJobClient)
    {
        _mapper = mapper;
        _deploymentDataProvider = deploymentDataProvider;
        _releaseDataProvider = releaseDataProvider;
        _environmentDataProvider = environmentDataProvider;
        _machineDataProvider = machineDataProvider;
        _serverTaskDataProvider = serverTaskDataProvider;
        _backgroundJobClient = backgroundJobClient;
    }

    public async Task<DeploymentCreatedEvent> CreateDeploymentAsync(CreateDeploymentCommand command, CancellationToken cancellationToken = default)
    {
        Log.Information("Creating deployment for release {ReleaseId} to environment {EnvironmentId}", 
            command.ReleaseId, command.EnvironmentId);

        // 1. 环境验证
        var isValid = await ValidateDeploymentEnvironmentAsync(command.ReleaseId, command.EnvironmentId, cancellationToken).ConfigureAwait(false);
        if (!isValid)
        {
            throw new DeploymentValidationException($"Environment validation failed for release {command.ReleaseId} and environment {command.EnvironmentId}");
        }

        // 2. 获取Release信息
        var release = await _releaseDataProvider.GetReleaseByIdAsync(command.ReleaseId, cancellationToken).ConfigureAwait(false);
        if (release == null)
        {
            throw new DeploymentEntityNotFoundException("Release", command.ReleaseId);
        }

        // 3. 创建ServerTask
        var serverTask = new Persistence.Entities.Deployments.ServerTask
        {
            Name = command.Name ?? $"Deploy {release.Version} to Environment",
            Description = $"Deploy release {release.Version} to environment {command.EnvironmentId}",
            QueueTime = DateTimeOffset.UtcNow,
            State = ServerTask.TaskState.Pending,
            ServerTaskType = "Deploy",
            ProjectId = release.ProjectId,
            EnvironmentId = command.EnvironmentId,
            SpaceId = release.SpaceId,
            LastModified = DateTimeOffset.UtcNow,
            BusinessProcessState = "Queued",
            StateOrder = 1,
            Weight = 1,
            BatchId = 0
        };

        await _serverTaskDataProvider.AddServerTaskAsync(serverTask, cancellationToken: cancellationToken).ConfigureAwait(false);

        // 4. 创建Deployment
        var deployment = new Persistence.Entities.Deployments.Deployment
        {
            Name = command.Name ?? $"Deploy {release.Version}",
            TaskId = serverTask.Id,
            SpaceId = release.SpaceId,
            ChannelId = release.ChannelId,
            ProjectId = release.ProjectId,
            ReleaseId = command.ReleaseId,
            EnvironmentId = command.EnvironmentId,
            DeployedBy = command.DeployedBy,
            Created = DateTimeOffset.Now,
            ProcessSnapshotId = release.ProjectDeploymentProcessSnapshotId,
            VariableSetSnapshotId = release.ProjectVariableSetSnapshotId,
            Json = JsonSerializer.Serialize(new
            {
                command.Comments,
                command.ForcePackageDownload,
                command.UseGuidedFailure,
                command.FormValues,
                command.SpecificMachineIds,
                command.ExcludedMachineIds
            })
        };

        await _deploymentDataProvider.AddDeploymentAsync(deployment, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Enqueue deployment execution via Hangfire
        var jobId = _backgroundJobClient.Enqueue<IDeploymentTaskExecutor>(
            executor => executor.ProcessAsync(serverTask.Id, CancellationToken.None),
            queue: "deployment");

        if (!string.IsNullOrEmpty(jobId))
        {
            serverTask.JobId = jobId;
            await _serverTaskDataProvider.UpdateServerTaskStateAsync(serverTask.Id, serverTask.State, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        Log.Information("Created deployment {DeploymentId} with task {TaskId}, job {JobId}", deployment.Id, serverTask.Id, jobId);

        return new DeploymentCreatedEvent
        {
            Deployment = _mapper.Map<DeploymentDto>(deployment),
            TaskId = serverTask.Id
        };
    }

    public async Task<bool> ValidateDeploymentEnvironmentAsync(int releaseId, int environmentId, CancellationToken cancellationToken = default)
    {
        Log.Information("Validating deployment environment for release {ReleaseId} and environment {EnvironmentId}", 
            releaseId, environmentId);

        // 1. 验证Release存在
        var release = await _releaseDataProvider.GetReleaseByIdAsync(releaseId, cancellationToken).ConfigureAwait(false);
        if (release == null)
        {
            Log.Warning("Release {ReleaseId} not found", releaseId);
            return false;
        }

        // 2. 验证Environment存在
        var environment = await _environmentDataProvider.GetEnvironmentByIdAsync(environmentId, cancellationToken).ConfigureAwait(false);
        if (environment == null)
        {
            Log.Warning("Environment {EnvironmentId} not found", environmentId);
            return false;
        }

        // 3. 验证环境中有可用的机器
        var environmentIds = new HashSet<int> { environmentId };
        var machines = await _machineDataProvider.GetMachinesByFilterAsync(environmentIds, new HashSet<string>(), cancellationToken).ConfigureAwait(false);
        
        if (!machines.Any())
        {
            Log.Warning("No available machines found in environment {EnvironmentId}", environmentId);
            return false;
        }

        Log.Information("Environment validation passed: found {MachineCount} available machines", machines.Count);
        return true;
    }
}
