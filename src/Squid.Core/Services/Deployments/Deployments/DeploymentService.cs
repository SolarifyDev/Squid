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

    public Task<DeploymentCreatedEvent> CreateDeploymentForTriggerAsync(CreateDeploymentCommand command, CancellationToken cancellationToken = default)
    {
        if (!_deploymentDataProvider.HasActiveTransaction)
            throw new InvalidOperationException("Trigger deployment creation requires the caller's transaction.");

        return PersistDeploymentAsync(command, isTriggerTask: true, cancellationToken);
    }

    public async Task<DeploymentCreatedEvent> CreateDeploymentAsync(CreateDeploymentCommand command, CancellationToken cancellationToken = default)
    {
        var created = await PersistDeploymentAsync(command, isTriggerTask: false, cancellationToken).ConfigureAwait(false);
        var taskId = created.TaskId;
        var jobId = NormalizeUtc(command.QueueTime) is { } time && time > DateTimeOffset.UtcNow
            ? _backgroundJobClient.Schedule<IDeploymentTaskExecutor>(executor => executor.ProcessAsync(taskId, CancellationToken.None, false), time)
            : _backgroundJobClient.Enqueue<IDeploymentTaskExecutor>(executor => executor.ProcessAsync(taskId, CancellationToken.None, false));

        if (!string.IsNullOrEmpty(jobId))
            await _serverTaskDataProvider.ReplaceJobIdAsync(taskId, expectedJobId: null, jobId, cancellationToken).ConfigureAwait(false);

        Log.Information("Queued deployment {DeploymentId} with task {TaskId}, job {JobId}", created.Deployment.Id, taskId, jobId);

        return created;
    }

    private async Task<DeploymentCreatedEvent> PersistDeploymentAsync(CreateDeploymentCommand command, bool isTriggerTask, CancellationToken ct)
    {
        Log.Information("Creating deployment for release {ReleaseId} to environment {EnvironmentId}", command.ReleaseId, command.EnvironmentId);

        var payload = BuildDeploymentRequestPayload(command);
        await ValidateCreationAsync(payload, ct).ConfigureAwait(false);
        var release = await _releaseDataProvider.GetReleaseByIdAsync(command.ReleaseId, ct).ConfigureAwait(false)
            ?? throw new DeploymentEntityNotFoundException("Release", command.ReleaseId);
        var deployedBy = _currentUser.Id ?? throw new InvalidOperationException("Current user id is required when creating deployment.");
        var task = await CreateServerTaskAsync(payload, release, isTriggerTask, ct).ConfigureAwait(false);
        var deployment = BuildDeployment(payload, release, task.Id, deployedBy);

        await _deploymentDataProvider.AddDeploymentAsync(deployment, cancellationToken: ct).ConfigureAwait(false);
        Log.Information("Created deployment {DeploymentId} with task {TaskId}", deployment.Id, task.Id);

        return new DeploymentCreatedEvent { TaskId = task.Id, Deployment = _mapper.Map<DeploymentDto>(deployment) };
    }

    private async Task ValidateCreationAsync(DeploymentRequestPayload payload, CancellationToken ct)
    {
        var preview = await PreviewInternalAsync(payload, DeploymentValidationStage.Create, ct).ConfigureAwait(false);

        if (preview.CanDeploy) return;

        var reasons = preview.BlockingReasons.Where(reason => !string.IsNullOrWhiteSpace(reason)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var message = reasons.Count == 0 ? "Deployment preview failed." : string.Join("; ", reasons);

        throw new DeploymentValidationException($"Deployment validation failed for release {payload.ReleaseId} and environment {payload.EnvironmentId}: {message}");
    }

    private async Task<Persistence.Entities.Deployments.ServerTask> CreateServerTaskAsync(
        DeploymentRequestPayload payload, Persistence.Entities.Deployments.Release release,
        bool isTriggerTask, CancellationToken ct)
    {
        var project = await _projectDataProvider.GetProjectByIdAsync(release.ProjectId, ct).ConfigureAwait(false);
        var environment = await _environmentDataProvider.GetEnvironmentByIdAsync(payload.EnvironmentId, ct).ConfigureAwait(false);
        var projectName = project?.Name ?? $"Project-{release.ProjectId}";
        var environmentName = environment?.Name ?? $"Environment-{payload.EnvironmentId}";

        var task = new Persistence.Entities.Deployments.ServerTask
        {
            Name = payload.Name ?? "Deploy",
            Description = $"Deploy {projectName} release {release.Version} to {environmentName}",
            QueueTime = payload.QueueTime ?? DateTimeOffset.UtcNow,
            State = TaskState.Pending,
            ServerTaskType = "Deploy",
            SpaceId = release.SpaceId,
            ProjectId = release.ProjectId,
            EnvironmentId = payload.EnvironmentId,
            LastModifiedDate = DateTimeOffset.UtcNow,
            BusinessProcessState = "Queued",
            StateOrder = 1,
            Weight = 1,
            ConcurrencyTag = $"deploy:env-{payload.EnvironmentId}",
            IsTriggerTask = isTriggerTask
        };

        await _serverTaskDataProvider.AddServerTaskAsync(task, cancellationToken: ct).ConfigureAwait(false);

        return task;
    }

    private static Persistence.Entities.Deployments.Deployment BuildDeployment(
        DeploymentRequestPayload payload, Persistence.Entities.Deployments.Release release, int taskId, int deployedBy)
    {
        return new Persistence.Entities.Deployments.Deployment
        {
            Name = payload.Name ?? $"Deploy {release.Version}",
            TaskId = taskId,
            SpaceId = release.SpaceId,
            ChannelId = release.ChannelId,
            ProjectId = release.ProjectId,
            ReleaseId = release.Id,
            EnvironmentId = payload.EnvironmentId,
            DeployedBy = deployedBy,
            CreatedDate = DateTimeOffset.UtcNow,
            ProcessSnapshotId = release.ProjectDeploymentProcessSnapshotId,
            VariableSetSnapshotId = release.ProjectVariableSetSnapshotId,
            Json = JsonSerializer.Serialize(payload)
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

    private static DeploymentRequestPayload BuildDeploymentRequestPayload(CreateDeploymentCommand command)
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
            QueueTime = NormalizeUtc(command.QueueTime),
            QueueTimeExpiry = NormalizeUtc(command.QueueTimeExpiry),
            FormValues = command.FormValues ?? new Dictionary<string, string>(),
            SpecificMachineIds = NormalizeMachineIds(command.SpecificMachineIds).OrderBy(id => id).ToList(),
            ExcludedMachineIds = NormalizeMachineIds(command.ExcludedMachineIds).OrderBy(id => id).ToList(),
            SkipActionIds = NormalizePositiveIds(command.SkipActionIds).OrderBy(id => id).ToList()
        };
    }
}
