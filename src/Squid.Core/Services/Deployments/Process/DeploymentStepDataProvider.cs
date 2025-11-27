namespace Squid.Core.Services.Deployments.Process;

public interface IDeploymentStepDataProvider : IScopedDependency
{
    Task AddDeploymentStepAsync(DeploymentStep step, bool forceSave = true, CancellationToken cancellationToken = default);

    Task AddDeploymentStepsAsync(List<DeploymentStep> steps, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdateDeploymentStepAsync(DeploymentStep step, bool forceSave = true, CancellationToken cancellationToken = default);

    Task DeleteDeploymentStepAsync(DeploymentStep step, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<DeploymentStep> GetDeploymentStepByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<List<DeploymentStep>> GetDeploymentStepsByProcessIdAsync(int processId, CancellationToken cancellationToken = default);

    Task DeleteDeploymentStepsByProcessIdAsync(int processId, CancellationToken cancellationToken = default);
}

public class DeploymentStepDataProvider : IDeploymentStepDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDeploymentStepPropertyDataProvider _stepPropertyDataProvider;
    private readonly IDeploymentActionDataProvider _actionDataProvider;

    public DeploymentStepDataProvider(IRepository repository, IUnitOfWork unitOfWork, 
        IDeploymentStepPropertyDataProvider stepPropertyDataProvider,
        IDeploymentActionDataProvider actionDataProvider)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _stepPropertyDataProvider = stepPropertyDataProvider;
        _actionDataProvider = actionDataProvider;
    }

    public async Task AddDeploymentStepAsync(DeploymentStep step, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(step, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task AddDeploymentStepsAsync(List<DeploymentStep> steps, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAllAsync(steps, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UpdateDeploymentStepAsync(DeploymentStep step, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.UpdateAsync(step, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteDeploymentStepAsync(DeploymentStep step, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _stepPropertyDataProvider.DeleteDeploymentStepPropertiesByStepIdAsync(step.Id, cancellationToken).ConfigureAwait(false);
        await _actionDataProvider.DeleteDeploymentActionsByStepIdAsync(step.Id, cancellationToken).ConfigureAwait(false);
        
        await _repository.DeleteAsync(step, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<DeploymentStep> GetDeploymentStepByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<DeploymentStep>(x => x.Id == id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<DeploymentStep>> GetDeploymentStepsByProcessIdAsync(int processId, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<DeploymentStep>()
            .Where(s => s.ProcessId == processId)
            .OrderBy(s => s.StepOrder)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteDeploymentStepsByProcessIdAsync(int processId, CancellationToken cancellationToken = default)
    {
        var steps = await GetDeploymentStepsByProcessIdAsync(processId, cancellationToken).ConfigureAwait(false);

        var stepIds = steps.Select(s => s.Id).ToList();

        await _stepPropertyDataProvider.DeleteDeploymentStepPropertiesByStepIdsAsync(stepIds, cancellationToken).ConfigureAwait(false);

        await _actionDataProvider.DeleteDeploymentActionsByStepIdsAsync(stepIds, cancellationToken).ConfigureAwait(false);

        await _repository.DeleteAllAsync(steps, cancellationToken).ConfigureAwait(false);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
