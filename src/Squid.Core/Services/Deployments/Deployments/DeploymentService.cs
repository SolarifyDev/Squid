using System.Text.Json;
using Squid.Core.Services.Jobs;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Planning;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Environments;
using Squid.Core.Services.Deployments.LifeCycle;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.Core.Services.Deployments.Validation;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.Identity;
using Squid.Core.Services.Machines;
using Squid.Message.Commands.Deployments.Deployment;
using Squid.Message.Events.Deployments.Deployment;
using Squid.Message.Models.Deployments.Deployment;

namespace Squid.Core.Services.Deployments.Deployments;

public partial class DeploymentService : IDeploymentService
{
    private readonly IMapper _mapper;
    private readonly ICurrentUser _currentUser;
    private readonly IDeploymentDataProvider _deploymentDataProvider;
    private readonly IReleaseDataProvider _releaseDataProvider;
    private readonly IEnvironmentDataProvider _environmentDataProvider;
    private readonly IMachineDataProvider _machineDataProvider;
    private readonly ILifecycleResolver _lifecycleResolver;
    private readonly ILifecycleProgressionEvaluator _progressionEvaluator;
    private readonly IDeploymentValidationOrchestrator _deploymentValidationOrchestrator;
    private readonly IDeploymentSnapshotService _deploymentSnapshotService;
    private readonly IServerTaskDataProvider _serverTaskDataProvider;
    private readonly IServerTaskService _serverTaskService;
    private readonly IProjectDataProvider _projectDataProvider;
    private readonly IActionHandlerRegistry _actionHandlerRegistry;
    private readonly IDeploymentPlanner _deploymentPlanner;
    private readonly ITransportRegistry _transportRegistry;
    private readonly ISquidBackgroundJobClient _backgroundJobClient;

    public DeploymentService(
        IMapper mapper,
        ICurrentUser currentUser,
        IDeploymentDataProvider deploymentDataProvider,
        IReleaseDataProvider releaseDataProvider,
        IEnvironmentDataProvider environmentDataProvider,
        IMachineDataProvider machineDataProvider,
        ILifecycleResolver lifecycleResolver,
        ILifecycleProgressionEvaluator progressionEvaluator,
        IDeploymentValidationOrchestrator deploymentValidationOrchestrator,
        IDeploymentSnapshotService deploymentSnapshotService,
        IServerTaskDataProvider serverTaskDataProvider,
        IServerTaskService serverTaskService,
        IProjectDataProvider projectDataProvider,
        IActionHandlerRegistry actionHandlerRegistry,
        IDeploymentPlanner deploymentPlanner,
        ITransportRegistry transportRegistry,
        ISquidBackgroundJobClient backgroundJobClient)
    {
        _mapper = mapper;
        _currentUser = currentUser;
        _deploymentDataProvider = deploymentDataProvider;
        _releaseDataProvider = releaseDataProvider;
        _environmentDataProvider = environmentDataProvider;
        _machineDataProvider = machineDataProvider;
        _lifecycleResolver = lifecycleResolver;
        _progressionEvaluator = progressionEvaluator;
        _deploymentValidationOrchestrator = deploymentValidationOrchestrator;
        _deploymentSnapshotService = deploymentSnapshotService;
        _serverTaskDataProvider = serverTaskDataProvider;
        _serverTaskService = serverTaskService;
        _projectDataProvider = projectDataProvider;
        _actionHandlerRegistry = actionHandlerRegistry;
        _deploymentPlanner = deploymentPlanner;
        _transportRegistry = transportRegistry;
        _backgroundJobClient = backgroundJobClient;
    }

    public async Task<DeploymentCreatedEvent> CreateDeploymentAsync(CreateDeploymentCommand command, CancellationToken cancellationToken = default)
    {
        Log.Information("Creating deployment for release {ReleaseId} to environment {EnvironmentId}", command.ReleaseId, command.EnvironmentId);

        var specificMachineIds = NormalizeMachineIds(command.SpecificMachineIds);
        var excludedMachineIds = NormalizeMachineIds(command.ExcludedMachineIds);
        var skipActionIds = NormalizePositiveIds(command.SkipActionIds);
        var queueTime = NormalizeUtc(command.QueueTime);
        var queueTimeExpiry = NormalizeUtc(command.QueueTimeExpiry);
        var deploymentRequestPayload = BuildDeploymentRequestPayload(command, queueTime, queueTimeExpiry, specificMachineIds, excludedMachineIds, skipActionIds);

        var preview = await PreviewInternalAsync(deploymentRequestPayload, DeploymentValidationStage.Create, cancellationToken).ConfigureAwait(false);

        if (!preview.CanDeploy)
        {
            var reasons = preview.BlockingReasons.Where(reason => !string.IsNullOrWhiteSpace(reason)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var message = reasons.Count == 0 ? "Deployment preview failed." : string.Join("; ", reasons);

            throw new DeploymentValidationException($"Deployment validation failed for release {command.ReleaseId} and environment {command.EnvironmentId}: {message}");
        }

        var release = await _releaseDataProvider.GetReleaseByIdAsync(command.ReleaseId, cancellationToken).ConfigureAwait(false);

        if (release == null)
            throw new DeploymentEntityNotFoundException("Release", command.ReleaseId);

        var project = await _projectDataProvider.GetProjectByIdAsync(release.ProjectId, cancellationToken).ConfigureAwait(false);
        var environment = await _environmentDataProvider.GetEnvironmentByIdAsync(command.EnvironmentId, cancellationToken).ConfigureAwait(false);

        var projectName = project?.Name ?? $"Project-{release.ProjectId}";
        var environmentName = environment?.Name ?? $"Environment-{command.EnvironmentId}";

        var effectiveQueueTime = queueTime ?? DateTimeOffset.UtcNow;
        var deployedBy = _currentUser.Id ?? throw new InvalidOperationException("Current user id is required when creating deployment.");

        var serverTask = new Persistence.Entities.Deployments.ServerTask
        {
            Name = command.Name ?? "Deploy",
            Description = $"Deploy {projectName} release {release.Version} to {environmentName}",
            QueueTime = effectiveQueueTime,
            State = TaskState.Pending,
            ServerTaskType = "Deploy",
            SpaceId = release.SpaceId,
            ProjectId = release.ProjectId,
            EnvironmentId = command.EnvironmentId,
            LastModifiedDate = DateTimeOffset.UtcNow,
            BusinessProcessState = "Queued",
            StateOrder = 1,
            Weight = 1,
            BatchId = 0,
            ConcurrencyTag = $"deploy:env-{command.EnvironmentId}"
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
            DeployedBy = deployedBy,
            CreatedDate = DateTimeOffset.UtcNow,
            ProcessSnapshotId = release.ProjectDeploymentProcessSnapshotId,
            VariableSetSnapshotId = release.ProjectVariableSetSnapshotId,
            Json = JsonSerializer.Serialize(deploymentRequestPayload)
        };

        await _deploymentDataProvider.AddDeploymentAsync(deployment, cancellationToken: cancellationToken).ConfigureAwait(false);

        var jobId = effectiveQueueTime > DateTimeOffset.UtcNow
            ? _backgroundJobClient.Schedule<IDeploymentTaskExecutor>(executor => executor.ProcessAsync(serverTask.Id, CancellationToken.None), effectiveQueueTime)
            : _backgroundJobClient.Enqueue<IDeploymentTaskExecutor>(executor => executor.ProcessAsync(serverTask.Id, CancellationToken.None));

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

    private static HashSet<int> NormalizePositiveIds(IEnumerable<int> ids)
    {
        if (ids == null)
            return new HashSet<int>();

        return ids.Where(id => id > 0).ToHashSet();
    }

    private static DateTimeOffset? NormalizeUtc(DateTimeOffset? value)
    {
        if (!value.HasValue)
            return null;

        var dateTime = value.Value;

        return dateTime.Offset == TimeSpan.Zero
            ? dateTime
            : dateTime.ToUniversalTime();
    }

    private static DeploymentRequestPayload BuildDeploymentRequestPayload(CreateDeploymentCommand command, DateTimeOffset? queueTime, DateTimeOffset? queueTimeExpiry, HashSet<int> specificMachineIds, HashSet<int> excludedMachineIds, HashSet<int> skipActionIds)
    {
        return new DeploymentRequestPayload
        {
            ReleaseId = command.ReleaseId,
            EnvironmentId = command.EnvironmentId,
            Name = command.Name,
            Comments = command.Comments,
            ForcePackageDownload = command.ForcePackageDownload,
            ForcePackageRedeployment = command.ForcePackageRedeployment,
            UseGuidedFailure = command.UseGuidedFailure,
            QueueTime = queueTime,
            QueueTimeExpiry = queueTimeExpiry,
            FormValues = command.FormValues ?? new Dictionary<string, string>(),
            SpecificMachineIds = specificMachineIds.OrderBy(id => id).ToList(),
            ExcludedMachineIds = excludedMachineIds.OrderBy(id => id).ToList(),
            SkipActionIds = skipActionIds.OrderBy(id => id).ToList()
        };
    }
}
