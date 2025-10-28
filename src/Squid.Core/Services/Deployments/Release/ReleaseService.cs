using Squid.Core.Services.Deployments.Process;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Variable;
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
        throw new NotImplementedException();
    }

    public async Task DeleteReleaseAsync(DeleteReleaseCommand command, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async Task<GetReleasesResponse> GetReleasesAsync(GetReleasesRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}