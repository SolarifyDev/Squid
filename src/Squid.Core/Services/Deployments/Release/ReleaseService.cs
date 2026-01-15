using Squid.Core.Services.Deployments.DeploymentCompletions;
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
}

public class ReleaseService : IReleaseService
{
    private readonly IMapper _mapper;
    private readonly IReleaseDataProvider _releaseDataProvider;
    private readonly IDeploymentCompletionDataProvider _deploymentCompletionDataProvider;
    private readonly IDeploymentSnapshotService _deploymentSnapshotService;
    
    public ReleaseService(IMapper mapper, IReleaseDataProvider releaseDataProvider, IDeploymentCompletionDataProvider deploymentCompletionDataProvider, IDeploymentSnapshotService deploymentSnapshotService)
    {
        _mapper = mapper;
        _releaseDataProvider = releaseDataProvider;
        _deploymentCompletionDataProvider = deploymentCompletionDataProvider;
        _deploymentSnapshotService = deploymentSnapshotService;
    }

    public async Task<ReleaseCreatedEvent> CreateReleaseAsync(CreateReleaseCommand command, CancellationToken cancellationToken = default)
    {
        var release = _mapper.Map<Persistence.Entities.Deployments.Release>(command);
        
        var variableSetSnapshot = await _deploymentSnapshotService
            .SnapshotVariableSetFromReleaseAsync(release, cancellationToken).ConfigureAwait(false);
        var deploymentProcessSnapshot = await _deploymentSnapshotService
            .SnapshotProcessFromReleaseAsync(release, cancellationToken).ConfigureAwait(false);
        
        release.ProjectVariableSetSnapshotId = variableSetSnapshot.Id;
        release.ProjectDeploymentProcessSnapshotId = deploymentProcessSnapshot.Id;
        
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
        
        var variableSetSnapshot = await _deploymentSnapshotService
            .SnapshotVariableSetFromReleaseAsync(release, cancellationToken).ConfigureAwait(false);
        
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
