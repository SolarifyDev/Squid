using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Message.Enums;
using Microsoft.EntityFrameworkCore.Storage;

namespace Squid.Core.Services.Deployments.Runbooks;

public interface IRunbookAggregateDataProvider : IScopedDependency
{
    Task<Runbook> CreateAsync(
        int projectId,
        int spaceId,
        string name,
        string description,
        string tagsJson,
        CancellationToken cancellationToken = default);

    Task<Runbook> UpdateMetadataAsync(
        Runbook runbook,
        string name,
        string description,
        string tagsJson,
        CancellationToken cancellationToken = default);

    Task<Runbook> CopyAsync(
        int sourceRunbookId,
        int projectId,
        int spaceId,
        string name,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        int runbookId,
        int projectId,
        int spaceId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Test seam for proving the aggregate transaction rolls back at each business
/// save boundary. Production leaves the default no-op implementation in place.
/// </summary>
public enum RunbookAggregateCreateStage
{
    ProcessInserted,
    VariableSetInserted,
    RunbookInserted
}

public enum RunbookAggregateCopyStage
{
    ProcessInserted,
    VariableSetInserted,
    StepsInserted,
    StepPropertiesInserted,
    ActionsInserted,
    ActionPropertiesInserted,
    ActionScopesInserted,
    RunbookInserted
}

public interface IRunbookAggregateWriteObserver : IScopedDependency
{
    Task AfterCreateStageAsync(
        RunbookAggregateCreateStage stage,
        int runbookId,
        int processId,
        int variableSetId,
        CancellationToken cancellationToken = default);

    Task AfterCopyStageAsync(
        RunbookAggregateCopyStage stage,
        int runbookId,
        int processId,
        int variableSetId,
        CancellationToken cancellationToken = default);

    Task BeforeBusinessSaveAsync(
        RunbookAggregateCreateStage stage,
        CancellationToken cancellationToken = default);

}

public sealed class NoOpRunbookAggregateWriteObserver : IRunbookAggregateWriteObserver
{
    public Task AfterCreateStageAsync(
        RunbookAggregateCreateStage stage,
        int runbookId,
        int processId,
        int variableSetId,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task AfterCopyStageAsync(
        RunbookAggregateCopyStage stage,
        int runbookId,
        int processId,
        int variableSetId,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task BeforeBusinessSaveAsync(
        RunbookAggregateCreateStage stage,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

}

public class RunbookAggregateDataProvider(
    SquidDbContext dbContext,
    IRepository repository,
    IUnitOfWork unitOfWork,
    IGenericDataProvider genericDataProvider,
    IRunbookAggregateWriteObserver writeObserver) : IRunbookAggregateDataProvider
{
    public async Task<Runbook> CreateAsync(
        int projectId,
        int spaceId,
        string name,
        string description,
        string tagsJson,
        CancellationToken cancellationToken = default)
    {
        return await genericDataProvider.ExecuteInTransactionAsync(async ct =>
        {
            var (runbookId, processId, variableSetId) = await ReserveAggregateIdsAsync(ct).ConfigureAwait(false);

            var process = new DeploymentProcess
            {
                Id = processId,
                ProjectId = projectId,
                SpaceId = spaceId,
                Version = 1,
                ProcessKind = DeploymentProcessKind.Runbook,
                OwnerId = runbookId
            };

            var variableSet = new VariableSet
            {
                Id = variableSetId,
                SpaceId = spaceId,
                Name = name,
                OwnerType = VariableSetOwnerType.Runbook,
                OwnerId = runbookId,
                Version = 1,
                RelatedDocumentIds = "[]"
            };

            var runbook = new Runbook
            {
                Id = runbookId,
                ProjectId = projectId,
                SpaceId = spaceId,
                Name = name,
                Description = description,
                TagsJson = tagsJson,
                ProcessId = processId,
                VariableSetId = variableSetId
            };

            await repository.InsertAsync(process, ct).ConfigureAwait(false);
            await writeObserver.AfterCreateStageAsync(
                RunbookAggregateCreateStage.ProcessInserted,
                runbookId,
                processId,
                variableSetId,
                ct).ConfigureAwait(false);

            await repository.InsertAsync(variableSet, ct).ConfigureAwait(false);
            await writeObserver.AfterCreateStageAsync(
                RunbookAggregateCreateStage.VariableSetInserted,
                runbookId,
                processId,
                variableSetId,
                ct).ConfigureAwait(false);

            await repository.InsertAsync(runbook, ct).ConfigureAwait(false);
            await writeObserver.AfterCreateStageAsync(
                RunbookAggregateCreateStage.RunbookInserted,
                runbookId,
                processId,
                variableSetId,
                ct).ConfigureAwait(false);

            return runbook;
        },
        ct => writeObserver.BeforeBusinessSaveAsync(
            RunbookAggregateCreateStage.RunbookInserted,
            ct),
        cancellationToken).ConfigureAwait(false);
    }

    public async Task<Runbook> UpdateMetadataAsync(
        Runbook runbook,
        string name,
        string description,
        string tagsJson,
        CancellationToken cancellationToken = default)
    {
        runbook.Name = name;
        runbook.Description = description;
        runbook.TagsJson = tagsJson;

        await repository.UpdateAsync(runbook, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return runbook;
    }

    public async Task<Runbook> CopyAsync(
        int sourceRunbookId,
        int projectId,
        int spaceId,
        string name,
        CancellationToken cancellationToken = default)
    {
        return await genericDataProvider.ExecuteInTransactionAsync(async ct =>
        {
            var source = await GetOwnedAggregateAsync(sourceRunbookId, projectId, spaceId, ct)
                .ConfigureAwait(false);

            var sourceSteps = await repository.Query<DeploymentStep>()
                .Where(s => s.ProcessId == source.Process.Id)
                .OrderBy(s => s.StepOrder)
                .ThenBy(s => s.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var sourceStepIds = sourceSteps.Select(s => s.Id).ToList();
            var sourceStepProperties = await repository.QueryNoTracking<DeploymentStepProperty>()
                .Where(p => sourceStepIds.Contains(p.StepId))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var sourceActions = await repository.Query<DeploymentAction>()
                .Where(a => sourceStepIds.Contains(a.StepId))
                .OrderBy(a => a.StepId)
                .ThenBy(a => a.ActionOrder)
                .ThenBy(a => a.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var sourceActionIds = sourceActions.Select(a => a.Id).ToList();
            var stepIdMap = await ReserveStepIdMapAsync(sourceStepIds, ct).ConfigureAwait(false);
            var actionIdMap = await ReserveActionIdMapAsync(sourceActionIds, ct).ConfigureAwait(false);

            var sourceActionProperties = await repository.QueryNoTracking<DeploymentActionProperty>()
                .Where(p => sourceActionIds.Contains(p.ActionId))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var sourceActionEnvironments = await repository.QueryNoTracking<ActionEnvironment>()
                .Where(s => sourceActionIds.Contains(s.ActionId))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var sourceActionExcludedEnvironments = await repository.QueryNoTracking<ActionExcludedEnvironment>()
                .Where(s => sourceActionIds.Contains(s.ActionId))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var sourceActionChannels = await repository.QueryNoTracking<ActionChannel>()
                .Where(s => sourceActionIds.Contains(s.ActionId))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var sourceActionMachineRoles = await repository.QueryNoTracking<ActionMachineRole>()
                .Where(s => sourceActionIds.Contains(s.ActionId))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var (runbookId, processId, variableSetId) = await ReserveAggregateIdsAsync(ct)
                .ConfigureAwait(false);

            var process = new DeploymentProcess
            {
                Id = processId,
                ProjectId = projectId,
                SpaceId = spaceId,
                Version = source.Process.Version,
                ProcessKind = DeploymentProcessKind.Runbook,
                OwnerId = runbookId
            };

            var variableSet = new VariableSet
            {
                Id = variableSetId,
                SpaceId = spaceId,
                Name = name,
                OwnerType = VariableSetOwnerType.Runbook,
                OwnerId = runbookId,
                Version = 1,
                RelatedDocumentIds = source.VariableSet.RelatedDocumentIds
            };

            var copiedSteps = sourceSteps.Select(s => new DeploymentStep
            {
                Id = stepIdMap[s.Id],
                ProcessId = processId,
                StepOrder = s.StepOrder,
                Name = s.Name,
                StepType = s.StepType,
                Condition = s.Condition,
                StartTrigger = s.StartTrigger,
                PackageRequirement = s.PackageRequirement,
                IsDisabled = s.IsDisabled,
                IsRequired = s.IsRequired
            }).ToList();

            var copiedStepProperties = sourceStepProperties.Select(p => new DeploymentStepProperty
            {
                StepId = stepIdMap[p.StepId],
                PropertyName = p.PropertyName,
                PropertyValue = p.PropertyValue
            }).ToList();

            var copiedActions = sourceActions.Select(a => new DeploymentAction
            {
                Id = actionIdMap[a.Id],
                StepId = stepIdMap[a.StepId],
                ActionOrder = a.ActionOrder,
                Name = a.Name,
                ActionType = a.ActionType,
                WorkerPoolId = a.WorkerPoolId,
                FeedId = a.FeedId,
                PackageId = a.PackageId,
                IsDisabled = a.IsDisabled,
                IsRequired = a.IsRequired,
                CanBeUsedForProjectVersioning = a.CanBeUsedForProjectVersioning
            }).ToList();

            var copiedActionProperties = sourceActionProperties.Select(p => new DeploymentActionProperty
            {
                ActionId = actionIdMap[p.ActionId],
                PropertyName = p.PropertyName,
                PropertyValue = p.PropertyValue
            }).ToList();
            var copiedActionEnvironments = sourceActionEnvironments.Select(s => new ActionEnvironment
            {
                ActionId = actionIdMap[s.ActionId],
                EnvironmentId = s.EnvironmentId
            }).ToList();
            var copiedActionExcludedEnvironments = sourceActionExcludedEnvironments
                .Select(s => new ActionExcludedEnvironment
                {
                    ActionId = actionIdMap[s.ActionId],
                    EnvironmentId = s.EnvironmentId
                }).ToList();
            var copiedActionChannels = sourceActionChannels.Select(s => new ActionChannel
            {
                ActionId = actionIdMap[s.ActionId],
                ChannelId = s.ChannelId
            }).ToList();
            var copiedActionMachineRoles = sourceActionMachineRoles.Select(s => new ActionMachineRole
            {
                ActionId = actionIdMap[s.ActionId],
                MachineRole = s.MachineRole
            }).ToList();

            var runbook = new Runbook
            {
                Id = runbookId,
                ProjectId = projectId,
                SpaceId = spaceId,
                Name = name,
                Description = source.Runbook.Description,
                TagsJson = source.Runbook.TagsJson,
                ProcessId = processId,
                VariableSetId = variableSetId
            };

            await repository.InsertAsync(process, ct).ConfigureAwait(false);
            await NotifyCopyAsync(RunbookAggregateCopyStage.ProcessInserted, runbookId, processId, variableSetId, ct)
                .ConfigureAwait(false);

            await repository.InsertAsync(variableSet, ct).ConfigureAwait(false);
            await NotifyCopyAsync(RunbookAggregateCopyStage.VariableSetInserted, runbookId, processId, variableSetId, ct)
                .ConfigureAwait(false);

            await InsertIfAnyAsync(copiedSteps, ct).ConfigureAwait(false);
            await NotifyCopyAsync(RunbookAggregateCopyStage.StepsInserted, runbookId, processId, variableSetId, ct)
                .ConfigureAwait(false);

            await InsertIfAnyAsync(copiedStepProperties, ct).ConfigureAwait(false);
            await NotifyCopyAsync(RunbookAggregateCopyStage.StepPropertiesInserted, runbookId, processId, variableSetId, ct)
                .ConfigureAwait(false);

            await InsertIfAnyAsync(copiedActions, ct).ConfigureAwait(false);
            await NotifyCopyAsync(RunbookAggregateCopyStage.ActionsInserted, runbookId, processId, variableSetId, ct)
                .ConfigureAwait(false);

            await InsertIfAnyAsync(copiedActionProperties, ct).ConfigureAwait(false);
            await NotifyCopyAsync(RunbookAggregateCopyStage.ActionPropertiesInserted, runbookId, processId, variableSetId, ct)
                .ConfigureAwait(false);

            await InsertIfAnyAsync(copiedActionEnvironments, ct).ConfigureAwait(false);
            await InsertIfAnyAsync(copiedActionExcludedEnvironments, ct).ConfigureAwait(false);
            await InsertIfAnyAsync(copiedActionChannels, ct).ConfigureAwait(false);
            await InsertIfAnyAsync(copiedActionMachineRoles, ct).ConfigureAwait(false);
            await NotifyCopyAsync(RunbookAggregateCopyStage.ActionScopesInserted, runbookId, processId, variableSetId, ct)
                .ConfigureAwait(false);

            await repository.InsertAsync(runbook, ct).ConfigureAwait(false);
            await NotifyCopyAsync(RunbookAggregateCopyStage.RunbookInserted, runbookId, processId, variableSetId, ct)
                .ConfigureAwait(false);
            return runbook;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(
        int runbookId,
        int projectId,
        int spaceId,
        CancellationToken cancellationToken = default)
    {
        await genericDataProvider.ExecuteInTransactionAsync(async ct =>
        {
            var aggregate = await GetOwnedAggregateAsync(runbookId, projectId, spaceId, ct)
                .ConfigureAwait(false);

            var variables = await repository.Query<Variable>()
                .Where(v => v.VariableSetId == aggregate.Runbook.VariableSetId)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var variableIds = variables.Select(v => v.Id).ToList();
            var variableScopes = await repository.Query<VariableScope>()
                .Where(s => variableIds.Contains(s.VariableId))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var steps = await repository.Query<DeploymentStep>()
                .Where(s => s.ProcessId == aggregate.Process.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var stepIds = steps.Select(s => s.Id).ToList();
            var stepProperties = await repository.Query<DeploymentStepProperty>()
                .Where(p => stepIds.Contains(p.StepId))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var actions = await repository.Query<DeploymentAction>()
                .Where(a => stepIds.Contains(a.StepId))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var actionIds = actions.Select(a => a.Id).ToList();
            var actionProperties = await repository.Query<DeploymentActionProperty>()
                .Where(p => actionIds.Contains(p.ActionId))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var actionEnvironments = await repository.Query<ActionEnvironment>()
                .Where(s => actionIds.Contains(s.ActionId))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var actionExcludedEnvironments = await repository.Query<ActionExcludedEnvironment>()
                .Where(s => actionIds.Contains(s.ActionId))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var actionChannels = await repository.Query<ActionChannel>()
                .Where(s => actionIds.Contains(s.ActionId))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var actionMachineRoles = await repository.Query<ActionMachineRole>()
                .Where(s => actionIds.Contains(s.ActionId))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            await DeleteIfAnyAsync(variableScopes, ct).ConfigureAwait(false);
            await DeleteIfAnyAsync(variables, ct).ConfigureAwait(false);
            await DeleteIfAnyAsync(actionProperties, ct).ConfigureAwait(false);
            await DeleteIfAnyAsync(actionEnvironments, ct).ConfigureAwait(false);
            await DeleteIfAnyAsync(actionExcludedEnvironments, ct).ConfigureAwait(false);
            await DeleteIfAnyAsync(actionChannels, ct).ConfigureAwait(false);
            await DeleteIfAnyAsync(actionMachineRoles, ct).ConfigureAwait(false);
            await DeleteIfAnyAsync(actions, ct).ConfigureAwait(false);
            await DeleteIfAnyAsync(stepProperties, ct).ConfigureAwait(false);
            await DeleteIfAnyAsync(steps, ct).ConfigureAwait(false);

            await repository.DeleteAsync(aggregate.Runbook, ct).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

            await repository.DeleteAsync(aggregate.Process, ct).ConfigureAwait(false);
            await repository.DeleteAsync(aggregate.VariableSet, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OwnedAggregate> GetOwnedAggregateAsync(
        int runbookId,
        int projectId,
        int spaceId,
        CancellationToken cancellationToken)
    {
        var runbook = await repository.Query<Runbook>()
            .FirstOrDefaultAsync(
                r => r.Id == runbookId && r.ProjectId == projectId && r.SpaceId == spaceId,
                cancellationToken)
            .ConfigureAwait(false);

        if (runbook == null)
        {
            throw new DeploymentEntityNotFoundException("Runbook", runbookId);
        }

        var process = await repository.Query<DeploymentProcess>()
            .FirstOrDefaultAsync(
                p => p.Id == runbook.ProcessId
                     && p.ProjectId == projectId
                     && p.SpaceId == spaceId
                     && p.ProcessKind == DeploymentProcessKind.Runbook
                     && p.OwnerId == runbook.Id,
                cancellationToken)
            .ConfigureAwait(false);

        if (process == null)
        {
            throw new DeploymentEntityNotFoundException(
                "RunbookProcess",
                runbook.ProcessId,
                "The process does not match the Runbook ownership chain.");
        }

        var variableSet = await repository.Query<VariableSet>()
            .FirstOrDefaultAsync(
                v => v.Id == runbook.VariableSetId
                     && v.SpaceId == spaceId
                     && v.OwnerType == VariableSetOwnerType.Runbook
                     && v.OwnerId == runbook.Id,
                cancellationToken)
            .ConfigureAwait(false);

        if (variableSet == null)
        {
            throw new DeploymentEntityNotFoundException(
                "RunbookVariableSet",
                runbook.VariableSetId,
                "The variable set does not match the Runbook ownership chain.");
        }

        return new OwnedAggregate(runbook, process, variableSet);
    }

    private async Task<(int RunbookId, int ProcessId, int VariableSetId)> ReserveAggregateIdsAsync(
        CancellationToken cancellationToken)
    {
        var runbookIds = await repository.SqlQueryRawAsync<int>(
            "SELECT nextval('runbook_id_seq')::integer AS \"Value\"").ConfigureAwait(false);
        var processIds = await repository.SqlQueryRawAsync<int>(
            "SELECT nextval('deployment_process_id_seq')::integer AS \"Value\"").ConfigureAwait(false);
        var variableSetIds = await repository.SqlQueryRawAsync<int>(
            "SELECT nextval('variable_set_id_seq')::integer AS \"Value\"").ConfigureAwait(false);

        return (runbookIds.Single(), processIds.Single(), variableSetIds.Single());
    }

    private async Task<Dictionary<int, int>> ReserveStepIdMapAsync(
        List<int> sourceIds,
        CancellationToken cancellationToken)
    {
        return await ReserveIdMapAsync("deployment_step_id_seq", sourceIds, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Dictionary<int, int>> ReserveActionIdMapAsync(
        List<int> sourceIds,
        CancellationToken cancellationToken)
    {
        return await ReserveIdMapAsync("deployment_action_id_seq", sourceIds, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Dictionary<int, int>> ReserveIdMapAsync(
        string sequenceName,
        List<int> sourceIds,
        CancellationToken cancellationToken)
    {
        var count = sourceIds?.Count ?? 0;
        if (count == 0) return new Dictionary<int, int>();

        var reserved = new List<int>(count);
        var connection = dbContext.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = $"SELECT nextval('{sequenceName}')::integer";

        for (var i = 0; i < count; i++)
        {
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            reserved.Add(Convert.ToInt32(value));
        }

        return sourceIds.Zip(reserved)
            .ToDictionary(pair => pair.First, pair => pair.Second);
    }

    private async Task InsertIfAnyAsync<TEntity>(List<TEntity> entities, CancellationToken cancellationToken)
        where TEntity : class, IEntity
    {
        if (entities.Count == 0) return;
        await repository.InsertAllAsync(entities, cancellationToken).ConfigureAwait(false);
    }

    private Task NotifyCopyAsync(
        RunbookAggregateCopyStage stage,
        int runbookId,
        int processId,
        int variableSetId,
        CancellationToken cancellationToken)
    {
        return writeObserver.AfterCopyStageAsync(
            stage,
            runbookId,
            processId,
            variableSetId,
            cancellationToken);
    }

    private async Task DeleteIfAnyAsync<TEntity>(List<TEntity> entities, CancellationToken cancellationToken)
        where TEntity : class, IEntity
    {
        if (entities.Count == 0) return;
        await repository.DeleteAllAsync(entities, cancellationToken).ConfigureAwait(false);
    }

    private sealed record OwnedAggregate(
        Runbook Runbook,
        DeploymentProcess Process,
        VariableSet VariableSet);
}
