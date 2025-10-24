using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Variable;
using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Events.Deployments.Release;
using Squid.Message.Models.Deployments.Release;

namespace Squid.Core.Services.Deployments.Release;

public interface IReleaseService : IScopedDependency
{
    Task<ReleaseCreatedEvent> CreateReleaseAsync(CreateReleaseCommand command, CancellationToken cancellationToken = default);
}

public class ReleaseService : IReleaseService
{
    private readonly IMapper _mapper;
    private readonly IReleaseDataProvider _releaseDataProvider;
    private readonly IProjectDataProvider _projectDataProvider;
    private readonly HybridVariableSnapshotService _hybridVariableSnapshotService;

    public ReleaseService(IMapper mapper, IReleaseDataProvider releaseDataProvider, IProjectDataProvider projectDataProvider, HybridVariableSnapshotService hybridVariableSnapshotService)
    {
        _mapper = mapper;
        _releaseDataProvider = releaseDataProvider;
        _projectDataProvider = projectDataProvider;
        _hybridVariableSnapshotService = hybridVariableSnapshotService;
    }

    public async Task<ReleaseCreatedEvent> CreateReleaseAsync(CreateReleaseCommand command, CancellationToken cancellationToken = default)
    {
        var release = _mapper.Map<Message.Domain.Deployments.Release>(command);
        
        var project = await _projectDataProvider.GetProjectByIdAsync(release.ProjectId, cancellationToken).ConfigureAwait(false);
        
        release.ProjectVariableSetSnapshotId = await _hybridVariableSnapshotService.GetOrCreateSnapshotAsync(project.VariableSetId, "user", cancellationToken).ConfigureAwait(false);
        // 查询project对应的deployment process snapshot填入release
        // release.ProjectDeploymentProcessSnapshotId = project.DeploymentProcessId;
        
        await _releaseDataProvider.CreateReleaseAsync(release, cancellationToken: cancellationToken).ConfigureAwait(false);
        
        return new ReleaseCreatedEvent
        {
            Release = _mapper.Map<ReleaseDto>(release)
        };
    }
}