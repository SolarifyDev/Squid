using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Channels;
using Squid.Core.Services.Deployments.DeploymentCompletions;
using Squid.Core.Services.Deployments.Environments;
using Squid.Core.Services.Deployments.LifeCycle;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Releases.Exceptions;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Events.Deployments.Release;
using Squid.Message.Models.Deployments.Release;
using Squid.Message.Requests.Deployments.Release;

namespace Squid.Core.Services.Deployments.Release;

public interface IReleaseService : IScopedDependency
{
    Task<ReleaseCreatedEvent> CreateReleaseAsync(CreateReleaseCommand command, CancellationToken cancellationToken = default);
    
    Task<ReleaseUpdatedEvent> UpdateReleaseAsync(UpdateReleaseCommand command, CancellationToken cancellationToken = default);
    
    Task DeleteReleaseAsync(DeleteReleaseCommand command, CancellationToken cancellationToken = default);
    
    Task<GetReleasesResponse> GetReleasesAsync(GetReleasesRequest request, CancellationToken cancellationToken = default);
    
    Task UpdateReleaseVariableAsync(UpdateReleaseVariableCommand command, CancellationToken cancellationToken = default);

    Task<GetReleaseVariableSnapshotResponse> GetReleaseVariableSnapshotAsync(GetReleaseVariableSnapshotRequest request, CancellationToken cancellationToken = default);

    Task<GetReleaseProgressionResponse> GetReleaseProgressionAsync(GetReleaseProgressionRequest request, CancellationToken cancellationToken = default);
}

public partial class ReleaseService : IReleaseService
{
    private readonly IMapper _mapper;
    private readonly IReleaseDataProvider _releaseDataProvider;
    private readonly IReleaseSelectedPackageDataProvider _releaseSelectedPackageDataProvider;
    private readonly IDeploymentCompletionDataProvider _deploymentCompletionDataProvider;
    private readonly IDeploymentSnapshotService _deploymentSnapshotService;
    private readonly IProjectDataProvider _projectDataProvider;
    private readonly IChannelDataProvider _channelDataProvider;
    private readonly IChannelVersionRuleDataProvider _channelVersionRuleDataProvider;
    private readonly IChannelVersionRuleValidator _channelVersionRuleValidator;
    private readonly ILifecycleResolver _lifecycleResolver;
    private readonly ILifecycleProgressionEvaluator _progressionEvaluator;
    private readonly ILifeCycleDataProvider _lifeCycleDataProvider;
    private readonly IEnvironmentDataProvider _environmentDataProvider;
    private readonly IRepository _repository;

    public ReleaseService(
        IMapper mapper,
        IReleaseDataProvider releaseDataProvider,
        IReleaseSelectedPackageDataProvider releaseSelectedPackageDataProvider,
        IDeploymentCompletionDataProvider deploymentCompletionDataProvider,
        IDeploymentSnapshotService deploymentSnapshotService,
        IProjectDataProvider projectDataProvider,
        IChannelDataProvider channelDataProvider,
        IChannelVersionRuleDataProvider channelVersionRuleDataProvider,
        IChannelVersionRuleValidator channelVersionRuleValidator,
        ILifecycleResolver lifecycleResolver,
        ILifecycleProgressionEvaluator progressionEvaluator,
        ILifeCycleDataProvider lifeCycleDataProvider,
        IEnvironmentDataProvider environmentDataProvider,
        IRepository repository)
    {
        _mapper = mapper;
        _releaseDataProvider = releaseDataProvider;
        _releaseSelectedPackageDataProvider = releaseSelectedPackageDataProvider;
        _deploymentCompletionDataProvider = deploymentCompletionDataProvider;
        _deploymentSnapshotService = deploymentSnapshotService;
        _projectDataProvider = projectDataProvider;
        _channelDataProvider = channelDataProvider;
        _channelVersionRuleDataProvider = channelVersionRuleDataProvider;
        _channelVersionRuleValidator = channelVersionRuleValidator;
        _lifecycleResolver = lifecycleResolver;
        _progressionEvaluator = progressionEvaluator;
        _lifeCycleDataProvider = lifeCycleDataProvider;
        _environmentDataProvider = environmentDataProvider;
        _repository = repository;
    }

    public async Task<ReleaseCreatedEvent> CreateReleaseAsync(CreateReleaseCommand command, CancellationToken cancellationToken = default)
    {
        var release = _mapper.Map<Persistence.Entities.Deployments.Release>(command);

        var project = await _projectDataProvider.GetProjectByIdAsync(command.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project == null)
            throw new ReleaseProjectNotFoundException(command.ProjectId);

        var channel = await _channelDataProvider.GetChannelByIdAsync(command.ChannelId, cancellationToken).ConfigureAwait(false);
        if (channel == null)
            throw new ReleaseChannelNotFoundException(command.ChannelId);

        if (channel.ProjectId != project.Id)
            throw new ReleaseChannelProjectMismatchException(command.ChannelId, project.Id, channel.ProjectId);

        if (channel.SpaceId != project.SpaceId)
            throw new ReleaseSpaceMismatchException(project.Id, channel.Id, project.SpaceId, channel.SpaceId);

        if (command.IgnoreChannelRules)
        {
            if (!project.AllowIgnoreChannelRules)
                throw new ChannelRulesCannotBeIgnoredException(project.Id);
        }
        else
        {
            await ValidateChannelVersionRulesAsync(command.ChannelId, command.SelectedPackages, cancellationToken).ConfigureAwait(false);
        }

        var existingRelease = await _releaseDataProvider
            .GetReleaseByVersionAsync(command.ProjectId, command.ChannelId, command.Version, cancellationToken).ConfigureAwait(false);

        if (existingRelease != null)
            throw new ReleaseDuplicateVersionException(command.ProjectId, command.ChannelId, command.Version);

        release.SpaceId = project.SpaceId;
        
        var variableSetSnapshot = await _deploymentSnapshotService
            .SnapshotVariableSetFromReleaseAsync(release, cancellationToken).ConfigureAwait(false);
        var deploymentProcessSnapshot = await _deploymentSnapshotService
            .SnapshotProcessFromReleaseAsync(release, cancellationToken).ConfigureAwait(false);
        
        release.ProjectVariableSetSnapshotId = variableSetSnapshot.Id;
        release.ProjectDeploymentProcessSnapshotId = deploymentProcessSnapshot.Id;
        
        await _releaseDataProvider.CreateReleaseAsync(release, forceSave: true, cancellationToken: cancellationToken).ConfigureAwait(false);

        await PersistSelectedPackagesAsync(release.Id, command.SelectedPackages, cancellationToken).ConfigureAwait(false);

        return new ReleaseCreatedEvent
        {
            Release = _mapper.Map<ReleaseDto>(release)
        };
    }

    public async Task<ReleaseUpdatedEvent> UpdateReleaseAsync(UpdateReleaseCommand command, CancellationToken cancellationToken = default)
    {
        if (command.Release == null)
            throw new ArgumentException("Release cannot be null", nameof(command.Release));

        var release = await _releaseDataProvider.GetReleaseByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);

        if (release == null)
            throw new Exception($"Release {command.Id} not found");

        _mapper.Map(command.Release, release);

        await _releaseDataProvider.UpdateReleaseAsync(release, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ReleaseUpdatedEvent
        {
            Release = _mapper.Map<ReleaseDto>(release)
        };
    }

    public async Task DeleteReleaseAsync(DeleteReleaseCommand command, CancellationToken cancellationToken = default)
    {
        var release = await _releaseDataProvider.GetReleaseByIdAsync(command.ReleaseId, cancellationToken).ConfigureAwait(false);

        if (release == null)
            throw new Exception($"Release {command.ReleaseId} not found");
        
        await _releaseDataProvider.DeleteReleaseAsync(release, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<GetReleasesResponse> GetReleasesAsync(GetReleasesRequest request, CancellationToken cancellationToken = default)
    {
        var (count, releases) = await _releaseDataProvider.GetReleasesAsync(request.PageIndex, request.PageSize, request.ProjectId, request.ChannelId, cancellationToken).ConfigureAwait(false);

        // 获取当前已部署的Release版本
        var currentDeployedReleaseIds = await GetCurrentDeployedReleaseIdsAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);

        return new GetReleasesResponse
        {
            Data = new GetReleasesResponseData
            {
                Count = count,
                Releases = _mapper.Map<List<ReleaseDto>>(releases),
                CurrentDeployedReleaseIds = currentDeployedReleaseIds
            }
        };
    }

    public async Task UpdateReleaseVariableAsync(UpdateReleaseVariableCommand command, CancellationToken cancellationToken = default)
    {
        var release = await _releaseDataProvider.GetReleaseByIdAsync(command.ReleaseId, cancellationToken).ConfigureAwait(false);

        if (release == null)
            throw new Exception($"Release {command.ReleaseId} not found");
        
        var variableSetSnapshot = await _deploymentSnapshotService
            .SnapshotVariableSetFromReleaseAsync(release, cancellationToken).ConfigureAwait(false);
        
        release.ProjectVariableSetSnapshotId = variableSetSnapshot.Id;
        
        await _releaseDataProvider.UpdateReleaseAsync(release, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<GetReleaseVariableSnapshotResponse> GetReleaseVariableSnapshotAsync(GetReleaseVariableSnapshotRequest request, CancellationToken cancellationToken = default)
    {
        var release = await _releaseDataProvider.GetReleaseByIdAsync(request.ReleaseId, cancellationToken).ConfigureAwait(false);

        if (release == null)
            throw new Exception($"Release {request.ReleaseId} not found");

        var snapshot = await _deploymentSnapshotService.LoadVariableSetSnapshotAsync(release.ProjectVariableSetSnapshotId, cancellationToken).ConfigureAwait(false);

        return new GetReleaseVariableSnapshotResponse
        {
            Data = new GetReleaseVariableSnapshotResponseData
            {
                VariableSnapshot = snapshot
            }
        };
    }

    private async Task PersistSelectedPackagesAsync(
        int releaseId, List<CreateReleaseSelectedPackageDto> selectedPackages, CancellationToken ct)
    {
        if (selectedPackages == null || selectedPackages.Count == 0) return;

        var entities = selectedPackages
            .Where(sp => !string.IsNullOrWhiteSpace(sp.ActionName))
            .Select(sp => new ReleaseSelectedPackage
            {
                ReleaseId = releaseId,
                ActionName = sp.ActionName,
                PackageReferenceName = sp.PackageReferenceName ?? string.Empty,
                Version = sp.Version ?? string.Empty
            });

        await _releaseSelectedPackageDataProvider.InsertAllAsync(entities, ct).ConfigureAwait(false);
    }

    private async Task ValidateChannelVersionRulesAsync(int channelId, List<CreateReleaseSelectedPackageDto> selectedPackages, CancellationToken ct)
    {
        if (selectedPackages == null || selectedPackages.Count == 0) return;

        var rules = await _channelVersionRuleDataProvider.GetRulesByChannelIdAsync(channelId, ct).ConfigureAwait(false);
        if (rules.Count == 0) return;

        var packages = selectedPackages
            .Where(sp => !string.IsNullOrWhiteSpace(sp.ActionName) && !string.IsNullOrWhiteSpace(sp.Version))
            .Select(sp => new SelectedPackageInfo(sp.ActionName, sp.Version))
            .ToList();

        var violations = _channelVersionRuleValidator.Validate(rules, packages);

        if (violations.Count > 0)
            throw new ReleaseVersionRuleViolationException(channelId, violations);
    }

    private async Task<List<int>> GetCurrentDeployedReleaseIdsAsync(int? projectId, CancellationToken cancellationToken)
    {
        try
        {
            var completions = await _deploymentCompletionDataProvider
                .GetLatestSuccessfulCompletionsAsync(projectId, cancellationToken).ConfigureAwait(false);

            if (completions.Count == 0) return new List<int>();

            var deploymentIds = completions.Select(c => c.DeploymentId).Distinct().ToList();

            var releaseIds = await _releaseDataProvider
                .GetReleaseIdsByDeploymentIdsAsync(deploymentIds, cancellationToken).ConfigureAwait(false);

            Log.Information("Found {Count} currently deployed releases for project {ProjectId}", releaseIds.Count, projectId);

            return releaseIds;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get current deployed release IDs for project {ProjectId}", projectId);
            return new List<int>();
        }
    }
}
