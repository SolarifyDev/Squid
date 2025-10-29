using Squid.Core.Services.Deployments.Process;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Variable;
using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Events.Deployments.Release;
using Squid.Message.Models.Deployments.Release;
using Squid.Message.Requests.Deployments.Release;
using Squid.Message.Requests;

namespace Squid.Core.Services.Deployments.Release;

public interface IReleaseService : IScopedDependency
{
    Task<ReleaseCreatedEvent> CreateReleaseAsync(CreateReleaseCommand command, CancellationToken cancellationToken = default);
    
    Task<ReleaseUpdatedEvent> UpdateReleaseAsync(UpdateReleaseCommand command, CancellationToken cancellationToken = default);
    
    Task DeleteReleaseAsync(DeleteReleaseCommand command, CancellationToken cancellationToken = default);
    
    Task<GetReleasesResponse> GetReleasesAsync(GetReleasesRequest request, CancellationToken cancellationToken = default);
}

public class ReleaseService : IReleaseService
{
    private readonly IMapper _mapper;
    private readonly IReleaseDataProvider _releaseDataProvider;
    private readonly IProjectDataProvider _projectDataProvider;
    private readonly HybridProcessSnapshotService _hybridProcessSnapshotService;
    private readonly HybridVariableSnapshotService _hybridVariableSnapshotService;

    public ReleaseService(IMapper mapper, IReleaseDataProvider releaseDataProvider, IProjectDataProvider projectDataProvider, HybridVariableSnapshotService hybridVariableSnapshotService, HybridProcessSnapshotService hybridProcessSnapshotService)
    {
        _mapper = mapper;
        _releaseDataProvider = releaseDataProvider;
        _projectDataProvider = projectDataProvider;
        _hybridProcessSnapshotService = hybridProcessSnapshotService;
        _hybridVariableSnapshotService = hybridVariableSnapshotService;
    }

    public async Task<ReleaseCreatedEvent> CreateReleaseAsync(CreateReleaseCommand command, CancellationToken cancellationToken = default)
    {
        var release = _mapper.Map<Message.Domain.Deployments.Release>(command);
        
        var project = await _projectDataProvider.GetProjectByIdAsync(release.ProjectId, cancellationToken).ConfigureAwait(false);
        
        release.ProjectVariableSetSnapshotId = await _hybridVariableSnapshotService.GetOrCreateSnapshotAsync(project.VariableSetId, "user", cancellationToken).ConfigureAwait(false);
        release.ProjectDeploymentProcessSnapshotId = await _hybridProcessSnapshotService.CreateSnapshotAsync(project.DeploymentProcessId, "user", cancellationToken).ConfigureAwait(false);
        
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
        
        await _releaseDataProvider.DeleteReleaseAsync(command.ReleaseId, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<GetReleasesResponse> GetReleasesAsync(GetReleasesRequest request, CancellationToken cancellationToken = default)
    {
        var releases = await _releaseDataProvider.GetReleasesAsync(cancellationToken).ConfigureAwait(false);

        return new GetReleasesResponse
        {
            Data = new GetReleasesResponseData
            {
                Releases = _mapper.Map<List<ReleaseDto>>(releases),
                Count = releases.Count,
                CurrentDeployedReleases = new List<ReleaseDto>() // 如有特殊逻辑可补充
            }
        };
    }
}
