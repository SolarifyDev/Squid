using Squid.Core.Services.Deployments.Process;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Variable;
using Squid.Core.Services.Deployments.DeploymentCompletions;
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
}

public class ReleaseService : IReleaseService
{
    private readonly IMapper _mapper;
    private readonly IReleaseDataProvider _releaseDataProvider;
    private readonly IProjectDataProvider _projectDataProvider;
    private readonly HybridProcessSnapshotService _hybridProcessSnapshotService;
    private readonly HybridVariableSnapshotService _hybridVariableSnapshotService;
    private readonly IDeploymentCompletionDataProvider _deploymentCompletionDataProvider;

    public ReleaseService(IMapper mapper, IReleaseDataProvider releaseDataProvider, IProjectDataProvider projectDataProvider, HybridVariableSnapshotService hybridVariableSnapshotService, HybridProcessSnapshotService hybridProcessSnapshotService, IDeploymentCompletionDataProvider deploymentCompletionDataProvider)
    {
        _mapper = mapper;
        _releaseDataProvider = releaseDataProvider;
        _projectDataProvider = projectDataProvider;
        _hybridProcessSnapshotService = hybridProcessSnapshotService;
        _hybridVariableSnapshotService = hybridVariableSnapshotService;
        _deploymentCompletionDataProvider = deploymentCompletionDataProvider;
    }

    public async Task<ReleaseCreatedEvent> CreateReleaseAsync(CreateReleaseCommand command, CancellationToken cancellationToken = default)
    {
        var release = _mapper.Map<Persistence.Entities.Deployments.Release>(command);
        
        var project = await _projectDataProvider.GetProjectByIdAsync(release.ProjectId, cancellationToken).ConfigureAwait(false);
        
        release.ProjectVariableSetSnapshotId = (await _hybridVariableSnapshotService.GetOrCreateSnapshotAsync(
            project.IncludedLibraryVariableSetIds.Split(',').Select(int.Parse).Concat([project.VariableSetId]).ToList(), "user", cancellationToken).ConfigureAwait(false)).Id;
        release.ProjectDeploymentProcessSnapshotId = (await _hybridProcessSnapshotService.GetOrCreateSnapshotAsync(project.DeploymentProcessId, "user", cancellationToken).ConfigureAwait(false)).Id;
        await _releaseDataProvider.CreateReleaseAsync(release, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ReleaseCreatedEvent
        {
            Release = _mapper.Map<ReleaseDto>(release)
        };
    }

    public async Task<ReleaseUpdatedEvent> UpdateReleaseAsync(UpdateReleaseCommand command, CancellationToken cancellationToken = default)
    {
        if (command.Release == null)
            throw new ArgumentException("Release cannot be null", nameof(command.Release));

        var release = await _releaseDataProvider.GetReleaseByIdAsync(command.Release.Id, cancellationToken).ConfigureAwait(false);

        if (release == null)
            throw new Exception($"Release {command.Release.Id} not found");

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
        
        var project = await _projectDataProvider.GetProjectByIdAsync(release.ProjectId, cancellationToken).ConfigureAwait(false);
        
        var variableSetSnapshot = await _hybridVariableSnapshotService.GetOrCreateSnapshotAsync(
            project.IncludedLibraryVariableSetIds.Split(',').Select(int.Parse).Concat([project.VariableSetId]).ToList(), "user", cancellationToken).ConfigureAwait(false);
        
        release.ProjectVariableSetSnapshotId = variableSetSnapshot.Id;
        await _releaseDataProvider.UpdateReleaseAsync(release, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<int>> GetCurrentDeployedReleaseIdsAsync(int? projectId, CancellationToken cancellationToken)
    {
        try
        {
            // 查询最新的成功部署完成记录，获取当前已部署的Release版本
            var completions = await _deploymentCompletionDataProvider.GetLatestSuccessfulCompletionsAsync(projectId, cancellationToken).ConfigureAwait(false);

            var releaseIds = completions
                    // TODO: 从project开始查LifeCycle=>Phase=>Environments=>Deployment=>Release
                .Distinct()
                .ToList();

            Log.Information("Found {Count} currently deployed releases for project {ProjectId}", releaseIds.Count, projectId);

            return [];
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get current deployed release IDs for project {ProjectId}", projectId);
            return new List<int>();
        }
    }
}
