using System.Text.Json;
using Squid.Core.Services.Jobs;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Commands.Deployments.Deployment;
using Squid.Message.Events.Deployments.Deployment;
using Squid.Message.Models.Deployments.Deployment;

namespace Squid.Core.Services.Deployments.Deployments;

public class DeploymentService : IDeploymentService
{
    private readonly IMapper _mapper;
    private readonly IDeploymentDataProvider _deploymentDataProvider;
    private readonly IReleaseDataProvider _releaseDataProvider;
    private readonly IDeploymentValidationService _deploymentValidationService;
    private readonly IServerTaskDataProvider _serverTaskDataProvider;
    private readonly ISquidBackgroundJobClient _backgroundJobClient;

    public DeploymentService(
        IMapper mapper,
        IDeploymentDataProvider deploymentDataProvider,
        IReleaseDataProvider releaseDataProvider,
        IDeploymentValidationService deploymentValidationService,
        IServerTaskDataProvider serverTaskDataProvider,
        ISquidBackgroundJobClient backgroundJobClient)
    {
        _mapper = mapper;
        _deploymentDataProvider = deploymentDataProvider;
        _releaseDataProvider = releaseDataProvider;
        _deploymentValidationService = deploymentValidationService;
        _serverTaskDataProvider = serverTaskDataProvider;
        _backgroundJobClient = backgroundJobClient;
    }

    public async Task<DeploymentCreatedEvent> CreateDeploymentAsync(CreateDeploymentCommand command, CancellationToken cancellationToken = default)
    {
        Log.Information("Creating deployment for release {ReleaseId} to environment {EnvironmentId}", command.ReleaseId, command.EnvironmentId);

        var specificMachineIds = NormalizeMachineIds(command.SpecificMachineIds);
        var excludedMachineIds = NormalizeMachineIds(command.ExcludedMachineIds);

        if (specificMachineIds.Overlaps(excludedMachineIds))
            throw new DeploymentValidationException("SpecificMachineIds and ExcludedMachineIds cannot overlap.");

        var validation = await _deploymentValidationService
            .ValidateDeploymentEnvironmentDetailedAsync(
                command.ReleaseId,
                command.EnvironmentId,
                specificMachineIds,
                excludedMachineIds,
                cancellationToken)
            .ConfigureAwait(false);

        if (!validation.IsValid)
            throw new DeploymentValidationException($"Environment validation failed for release {command.ReleaseId} and environment {command.EnvironmentId}: {validation.Message}");

        var release = await _releaseDataProvider.GetReleaseByIdAsync(command.ReleaseId, cancellationToken).ConfigureAwait(false);
        
        if (release == null) 
            throw new DeploymentEntityNotFoundException("Release", command.ReleaseId);

        var serverTask = new Persistence.Entities.Deployments.ServerTask
        {
            Name = command.Name ?? $"Deploy {release.Version} to Environment",
            Description = $"Deploy release {release.Version} to environment {command.EnvironmentId}",
            QueueTime = DateTimeOffset.UtcNow,
            State = TaskState.Pending,
            ServerTaskType = "Deploy",
            SpaceId = release.SpaceId,
            ProjectId = release.ProjectId,
            EnvironmentId = command.EnvironmentId,
            LastModified = DateTimeOffset.UtcNow,
            BusinessProcessState = "Queued",
            StateOrder = 1,
            Weight = 1,
            BatchId = 0
        };

        await _serverTaskDataProvider.AddServerTaskAsync(serverTask, cancellationToken: cancellationToken).ConfigureAwait(false);

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
            Created = DateTimeOffset.UtcNow,
            ProcessSnapshotId = release.ProjectDeploymentProcessSnapshotId,
            VariableSetSnapshotId = release.ProjectVariableSetSnapshotId,
            Json = JsonSerializer.Serialize(new DeploymentRequestPayload
            {
                Comments = command.Comments,
                ForcePackageDownload = command.ForcePackageDownload,
                UseGuidedFailure = command.UseGuidedFailure,
                FormValues = command.FormValues ?? new Dictionary<string, string>(),
                SpecificMachineIds = specificMachineIds.ToList(),
                ExcludedMachineIds = excludedMachineIds.ToList()
            })
        };

        await _deploymentDataProvider.AddDeploymentAsync(deployment, cancellationToken: cancellationToken).ConfigureAwait(false);

        var jobId = _backgroundJobClient.Enqueue<IDeploymentTaskExecutor>(executor => executor.ProcessAsync(serverTask.Id, CancellationToken.None));

        if (!string.IsNullOrEmpty(jobId))
        {
            serverTask.JobId = jobId;
            
            await _serverTaskDataProvider.UpdateServerTaskStateAsync(serverTask.Id, serverTask.State, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        Log.Information("Created deployment {DeploymentId} with task {TaskId}, job {JobId}", deployment.Id, serverTask.Id, jobId);

        return new DeploymentCreatedEvent
        {
            TaskId = serverTask.Id,
            Deployment = _mapper.Map<DeploymentDto>(deployment)
        };
    }

    private static HashSet<int> NormalizeMachineIds(IEnumerable<string> machineIds)
    {
        if (machineIds == null)
            return new HashSet<int>();

        var ids = new HashSet<int>();

        foreach (var raw in machineIds)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            if (int.TryParse(raw.Trim(), out var parsed) && parsed > 0)
                ids.Add(parsed);
        }

        return ids;
    }

}
