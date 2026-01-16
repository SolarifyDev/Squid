using Squid.Core.Persistence.Db;

namespace Squid.Core.Services.Deployments.Snapshots;

public partial interface IDeploymentSnapshotDataProvider : IScopedDependency
{
    
}

public partial class DeploymentSnapshotDataProvider : IDeploymentSnapshotDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public DeploymentSnapshotDataProvider(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }
}