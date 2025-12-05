namespace Squid.Core.Services.Deployments.Process.Action;

public interface IDeploymentActionPropertyDataProvider : IScopedDependency
{
    Task AddDeploymentActionPropertiesAsync(List<DeploymentActionProperty> properties, CancellationToken cancellationToken = default);

    Task UpdateDeploymentActionPropertiesAsync(int actionId, List<DeploymentActionProperty> properties, CancellationToken cancellationToken = default);

    Task DeleteDeploymentActionPropertiesByActionIdAsync(int actionId, CancellationToken cancellationToken = default);

    Task DeleteDeploymentActionPropertiesByActionIdsAsync(List<int> actionIds, CancellationToken cancellationToken = default);

    Task<List<DeploymentActionProperty>> GetDeploymentActionPropertiesByActionIdAsync(int actionId, CancellationToken cancellationToken = default);

    Task<List<DeploymentActionProperty>> GetDeploymentActionPropertiesByActionIdsAsync(List<int> actionIds, CancellationToken cancellationToken = default);
}

public class DeploymentActionPropertyDataProvider : IDeploymentActionPropertyDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public DeploymentActionPropertyDataProvider(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task AddDeploymentActionPropertiesAsync(List<DeploymentActionProperty> properties, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAllAsync(properties, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateDeploymentActionPropertiesAsync(int actionId, List<DeploymentActionProperty> properties, CancellationToken cancellationToken = default)
    {
        await DeleteDeploymentActionPropertiesByActionIdAsync(actionId, cancellationToken).ConfigureAwait(false);
        await AddDeploymentActionPropertiesAsync(properties, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteDeploymentActionPropertiesByActionIdAsync(int actionId, CancellationToken cancellationToken = default)
    {
        var properties = await _repository.Query<DeploymentActionProperty>()
            .Where(p => p.ActionId == actionId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        await _repository.DeleteAllAsync(properties, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteDeploymentActionPropertiesByActionIdsAsync(List<int> actionIds, CancellationToken cancellationToken = default)
    {
        var properties = await _repository.Query<DeploymentActionProperty>()
            .Where(p => actionIds.Contains(p.ActionId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        await _repository.DeleteAllAsync(properties, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<DeploymentActionProperty>> GetDeploymentActionPropertiesByActionIdAsync(int actionId, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<DeploymentActionProperty>()
            .Where(p => p.ActionId == actionId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<DeploymentActionProperty>> GetDeploymentActionPropertiesByActionIdsAsync(List<int> actionIds, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<DeploymentActionProperty>()
            .Where(p => actionIds.Contains(p.ActionId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
