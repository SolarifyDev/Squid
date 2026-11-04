using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Enums;

namespace Squid.Core.Services.Deployments.Runbooks;

public interface IRunbookDataProvider : IScopedDependency
{
    Task<Runbook> GetRunbookByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<Runbook> GetRunbookByIdAndProjectAsync(
        int id,
        int projectId,
        CancellationToken cancellationToken = default);

    Task<(int Count, List<Runbook> Runbooks)> GetRunbookPagingAsync(
        int projectId,
        int spaceId,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<DeploymentProcess> GetRunbookProcessAsync(
        int runbookId,
        int processId,
        int projectId,
        int spaceId,
        CancellationToken cancellationToken = default);

    Task<VariableSet> GetRunbookVariableSetAsync(
        int runbookId,
        int spaceId,
        CancellationToken cancellationToken = default);

    Task<DeploymentProcess> GetProjectDeploymentProcessAsync(
        int projectId,
        int processId,
        int spaceId,
        CancellationToken cancellationToken = default);
}

public class RunbookDataProvider(IRepository repository) : IRunbookDataProvider
{
    public async Task<Runbook> GetRunbookByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        return await repository.Query<Runbook>(r => r.Id == id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Runbook> GetRunbookByIdAndProjectAsync(
        int id,
        int projectId,
        CancellationToken cancellationToken = default)
    {
        return await repository.Query<Runbook>(r => r.Id == id && r.ProjectId == projectId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(int Count, List<Runbook> Runbooks)> GetRunbookPagingAsync(
        int projectId,
        int spaceId,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = repository.Query<Runbook>()
            .Where(r => r.ProjectId == projectId && r.SpaceId == spaceId);

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var runbooks = await query
            .OrderBy(r => r.Name)
            .ThenBy(r => r.Id)
            .Skip((pageIndex - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (count, runbooks);
    }

    public async Task<DeploymentProcess> GetRunbookProcessAsync(
        int runbookId,
        int processId,
        int projectId,
        int spaceId,
        CancellationToken cancellationToken = default)
    {
        return await (
                from runbook in repository.Query<Runbook>()
                join process in repository.Query<DeploymentProcess>()
                    on runbook.ProcessId equals process.Id
                where runbook.Id == runbookId
                      && process.Id == processId
                      && runbook.ProjectId == projectId
                      && runbook.SpaceId == spaceId
                      && process.ProjectId == projectId
                      && process.SpaceId == spaceId
                      && process.ProcessKind == DeploymentProcessKind.Runbook
                      && process.OwnerId == runbook.Id
                select process)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<VariableSet> GetRunbookVariableSetAsync(
        int runbookId,
        int spaceId,
        CancellationToken cancellationToken = default)
    {
        return await (
                from runbook in repository.Query<Runbook>()
                join variableSet in repository.Query<VariableSet>()
                    on runbook.VariableSetId equals variableSet.Id
                where runbook.Id == runbookId
                      && runbook.SpaceId == spaceId
                      && variableSet.SpaceId == spaceId
                      && variableSet.OwnerType == VariableSetOwnerType.Runbook
                      && variableSet.OwnerId == runbook.Id
                select variableSet)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DeploymentProcess> GetProjectDeploymentProcessAsync(
        int projectId,
        int processId,
        int spaceId,
        CancellationToken cancellationToken = default)
    {
        return await (
                from project in repository.Query<Persistence.Entities.Deployments.Project>()
                join process in repository.Query<DeploymentProcess>()
                    on project.DeploymentProcessId equals process.Id
                where project.Id == projectId
                      && project.SpaceId == spaceId
                      && process.Id == processId
                      && process.ProjectId == projectId
                      && process.SpaceId == spaceId
                      && process.ProcessKind == DeploymentProcessKind.ProjectDeployment
                      && process.OwnerId == null
                select process)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
