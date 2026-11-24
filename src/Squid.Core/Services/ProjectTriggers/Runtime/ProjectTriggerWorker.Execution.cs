using System.Text.Json;
using FluentValidation;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization.Exceptions;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Planning.Exceptions;
using Squid.Core.Services.ProjectTriggers.Actions;
using Squid.Core.Services.ProjectTriggers.Management;
using Squid.Message.Models.ProjectTriggers;

namespace Squid.Core.Services.ProjectTriggers.Runtime;

public sealed partial class ProjectTriggerWorker
{
    private async Task<bool> ExecuteOneAsync(ILifetimeScope scope, ProjectTriggerRun candidate, CancellationToken ct)
    {
        var dataProvider = scope.Resolve<IProjectTriggerRunDataProvider>();
        var prepared = await PrepareAsync(scope, dataProvider, candidate, ct).ConfigureAwait(false);

        await using var transaction = await dataProvider.BeginRunTransactionAsync(ct).ConfigureAwait(false);
        var run = await dataProvider.LockRunAsync(
            candidate.Id, ProjectTriggerRunStatuses.Pending, _time.GetUtcNow(), ct).ConfigureAwait(false);

        if (run == null || run.ExecutionUserId != candidate.ExecutionUserId ||
            run.ActionPlanJson != candidate.ActionPlanJson) return false;

        var trigger = await dataProvider.LockExecutionTriggerAsync(run.TriggerId, ct).ConfigureAwait(false);
        var changed = await ExecuteLockedRunAsync(scope, dataProvider, trigger, run, prepared, ct).ConfigureAwait(false);

        if (!changed) return false;

        await dataProvider.CommitRunAsync(ct).ConfigureAwait(false);

        return true;
    }

    private async Task<bool> ExecuteLockedRunAsync(
        ILifetimeScope scope,
        IProjectTriggerRunDataProvider dataProvider,
        ProjectTrigger trigger,
        ProjectTriggerRun run,
        PreparedAction prepared,
        CancellationToken ct)
    {
        var inactive = ProjectTriggerRunPolicy.InactiveReason(trigger, run);

        if (inactive != null)
        {
            run.Status = ProjectTriggerRunStatuses.Skipped;
            run.Reason = inactive;
            run.CompletedAt = _time.GetUtcNow();
        }
        else if (prepared == null || prepared.Definition != ProjectTriggerObserver.Definition(trigger))
        {
            return false;
        }
        else if (prepared.Failure != null)
        {
            ProjectTriggerRunPolicy.RecordFailure(run, _time.GetUtcNow(), prepared.Failure.Reason, prepared.Failure.Retryable);
        }
        else
        {
            await RunBusinessActionAsync(scope, dataProvider, run, prepared, ct).ConfigureAwait(false);
        }

        return true;
    }

    private static async Task<PreparedAction> PrepareAsync(
        ILifetimeScope scope, IProjectTriggerRunDataProvider dataProvider, ProjectTriggerRun run, CancellationToken ct)
    {
        var trigger = await dataProvider.GetTriggerAsync(run.TriggerId, ct).ConfigureAwait(false);

        if (ProjectTriggerRunPolicy.InactiveReason(trigger, run) != null) return null;

        var definition = ProjectTriggerObserver.Definition(trigger);

        try
        {
            var handler = scope.Resolve<IProjectTriggerRegistry>().Resolve(run.TriggerType, run.ActionType);
            var allowed = await handler.ValidateAsync(definition, ct).ConfigureAwait(false);
            var plan = JsonSerializer.Deserialize<TriggerActionPlan>(run.ActionPlanJson);

            if (plan == null || !ProjectTriggerRunPolicy.MatchesScope(plan, allowed))
                return new PreparedAction(definition, null,
                    new("The saved action plan does not match the trigger's resource scope.", false));

            return new PreparedAction(definition, plan, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new PreparedAction(definition, null, Failure(ex));
        }
    }

    private async Task RunBusinessActionAsync(
        ILifetimeScope scope,
        IProjectTriggerRunDataProvider dataProvider,
        ProjectTriggerRun run,
        PreparedAction prepared,
        CancellationToken ct)
    {
        // Keep the run lock while undoing already-flushed business writes and recording failure/backoff.
        await dataProvider.CreateActionSavepointAsync(ct).ConfigureAwait(false);

        try
        {
            var resources = ProjectTriggerRunPolicy.ActualScope(prepared.Plan);

            await scope.Resolve<IProjectTriggerAccess>()
                .EnsureActionAsync(run.ExecutionUserId, prepared.Definition, resources, ct).ConfigureAwait(false);

            var result = await scope.Resolve<ITriggerBusinessActions>()
                .ExecuteAsync(prepared.Definition, run.ActionType, prepared.Plan, ct).ConfigureAwait(false);

            await dataProvider.SaveBusinessActionAsync(ct).ConfigureAwait(false);
            RecordResult(run, result, _time.GetUtcNow());
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await dataProvider.RollbackActionAsync(run, ct).ConfigureAwait(false);

            var failure = Failure(ex);

            ProjectTriggerRunPolicy.RecordFailure(run, _time.GetUtcNow(), failure.Reason, failure.Retryable);
            Log.Warning("Project trigger run {RunId} action failed ({ErrorType})", run.Id, ex.GetType().Name);
        }
    }

    private static void RecordResult(ProjectTriggerRun run, TriggerActionResult result, DateTimeOffset now)
    {
        if (result == null || result.ReleaseId <= 0 || result.DeploymentId.HasValue != result.TaskId.HasValue ||
            result.DeploymentId is <= 0 || result.TaskId is <= 0)
            throw new InvalidOperationException("Incomplete trigger business result.");

        run.Attempts++;
        run.ReleaseId = result.ReleaseId;
        run.DeploymentId = result.DeploymentId;
        run.TaskId = result.TaskId;
        run.Reason = null;
        run.Status = result.TaskId.HasValue ? ProjectTriggerRunStatuses.AwaitingDispatch : ProjectTriggerRunStatuses.ReleaseCreated;
        run.NextAttemptAt = now;
        run.CompletedAt = result.TaskId.HasValue ? null : now;
    }

    private static ActionFailure Failure(Exception ex)
    {
        if (PostgresTransientFailureClassifier.IsTransient(ex))
            return new("The business action encountered a temporary database or service failure.", true);

        return ex switch
        {
            PermissionDeniedException => new("Execution authorization was denied.", false),
            ProjectTriggerNotFoundException => new("The trigger project is no longer available.", false),
            TriggerActionRejectedException rejected => new(rejected.Message, false),
            DeploymentPlanValidationException or DeploymentValidationException =>
                new("Deployment preflight or lifecycle rules blocked the action.", false),
            JsonException => new("The saved action plan is invalid.", false),
            ValidationException or ArgumentException or InvalidOperationException or KeyNotFoundException =>
                new("The trigger configuration or business preconditions are no longer valid.", false),
            _ => new("The business action could not be completed.", true)
        };
    }

    private sealed record PreparedAction(ProjectTriggerDefinition Definition, TriggerActionPlan Plan, ActionFailure Failure);
    private sealed record ActionFailure(string Reason, bool Retryable);
}
