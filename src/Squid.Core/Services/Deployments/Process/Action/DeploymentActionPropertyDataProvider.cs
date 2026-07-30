using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

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
        var normalized = NormalizeProperties(properties);

        if (normalized.Count == 0) return;

        await _repository.InsertAllAsync(normalized, cancellationToken).ConfigureAwait(false);
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

    private static List<DeploymentActionProperty> NormalizeProperties(List<DeploymentActionProperty> properties)
    {
        if (properties == null || properties.Count == 0) return new List<DeploymentActionProperty>();

        var normalized = new List<DeploymentActionProperty>();
        var indexesByKey = new Dictionary<ActionPropertyKey, int>(ActionPropertyKeyComparer.Instance);

        foreach (var property in properties)
        {
            if (property == null) continue;

            var key = new ActionPropertyKey(property.ActionId, property.PropertyName);

            if (indexesByKey.TryGetValue(key, out var index))
            {
                normalized[index].PropertyValue = property.PropertyValue;
                continue;
            }

            indexesByKey[key] = normalized.Count;
            normalized.Add(property);
        }

        return normalized;
    }

    private readonly record struct ActionPropertyKey(int ActionId, string PropertyName);

    private sealed class ActionPropertyKeyComparer : IEqualityComparer<ActionPropertyKey>
    {
        public static readonly ActionPropertyKeyComparer Instance = new();

        public bool Equals(ActionPropertyKey x, ActionPropertyKey y)
        {
            return x.ActionId == y.ActionId
                   && string.Equals(x.PropertyName, y.PropertyName, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(ActionPropertyKey obj)
        {
            return HashCode.Combine(obj.ActionId, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.PropertyName ?? string.Empty));
        }
    }
}
