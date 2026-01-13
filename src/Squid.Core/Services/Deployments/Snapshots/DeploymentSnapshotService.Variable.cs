using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Deployments.Snapshots;

public partial interface IDeploymentSnapshotService
{
    Task<VariableSetSnapshot> SnapshotVariableSetFromReleaseAsync(Persistence.Entities.Deployments.Release release, CancellationToken cancellationToken = default);
}

public partial class DeploymentSnapshotService
{
    public Task<VariableSetSnapshot> SnapshotVariableSetFromReleaseAsync(Persistence.Entities.Deployments.Release release, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}