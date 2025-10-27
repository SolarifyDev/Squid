using Squid.Core.DependencyInjection;
using Squid.Core.Persistence;
using Squid.Message.Domain.Deployments;

namespace Squid.Core.Services.Deployments.Process;

public interface IDeploymentStepPropertyDataProvider : IScopedDependency
{
    Task AddDeploymentStepPropertiesAsync(List<DeploymentStepProperty> properties, CancellationToken cancellationToken = default);

    Task UpdateDeploymentStepPropertiesAsync(int stepId, List<DeploymentStepProperty> properties, CancellationToken cancellationToken = default);

    Task DeleteDeploymentStepPropertiesByStepIdAsync(int stepId, CancellationToken cancellationToken = default);

    Task DeleteDeploymentStepPropertiesByStepIdsAsync(List<int> stepIds, CancellationToken cancellationToken = default);

    Task<List<DeploymentStepProperty>> GetDeploymentStepPropertiesByStepIdAsync(int stepId, CancellationToken cancellationToken = default);

    Task<List<DeploymentStepProperty>> GetDeploymentStepPropertiesByStepIdsAsync(List<int> stepIds, CancellationToken cancellationToken = default);
}

public class DeploymentStepPropertyDataProvider : IDeploymentStepPropertyDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public DeploymentStepPropertyDataProvider(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task AddDeploymentStepPropertiesAsync(List<DeploymentStepProperty> properties, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAllAsync(properties, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateDeploymentStepPropertiesAsync(int stepId, List<DeploymentStepProperty> properties, CancellationToken cancellationToken = default)
    {
        await DeleteDeploymentStepPropertiesByStepIdAsync(stepId, cancellationToken).ConfigureAwait(false);
        await AddDeploymentStepPropertiesAsync(properties, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteDeploymentStepPropertiesByStepIdAsync(int stepId, CancellationToken cancellationToken = default)
    {
        var properties = await _repository.Query<DeploymentStepProperty>()
            .Where(p => p.StepId == stepId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        await _repository.DeleteAllAsync(properties, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteDeploymentStepPropertiesByStepIdsAsync(List<int> stepIds, CancellationToken cancellationToken = default)
    {
        var properties = await _repository.Query<DeploymentStepProperty>()
            .Where(p => stepIds.Contains(p.StepId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        await _repository.DeleteAllAsync(properties, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<DeploymentStepProperty>> GetDeploymentStepPropertiesByStepIdAsync(int stepId, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<DeploymentStepProperty>()
            .Where(p => p.StepId == stepId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<DeploymentStepProperty>> GetDeploymentStepPropertiesByStepIdsAsync(List<int> stepIds, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<DeploymentStepProperty>()
            .Where(p => stepIds.Contains(p.StepId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
