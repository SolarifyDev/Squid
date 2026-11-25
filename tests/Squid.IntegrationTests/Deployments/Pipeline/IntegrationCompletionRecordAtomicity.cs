using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.ServerTask;

namespace Squid.IntegrationTests.Deployments.Pipeline;

/// <summary>
/// Fidelity: HIGH (real Postgres, the real completion handler, data providers and unit of work). The terminal state write and the DeploymentCompletion
/// row are one transaction: lifecycle progression and auto-deploy read the row, so a task that is Success/Failed/Cancelled without it blocks the next
/// phase forever. The insert is made to fail by a real CHECK constraint on the table, not by a mock.
/// </summary>
public class IntegrationCompletionRecordAtomicity : DeploymentFixtureBase
{
    private const string RejectAll = "ck_test_reject_completion";
    private const string Owner = "me:1:run";

    public enum Outcome { Success, Failure, Cancelled }

    [Theory]
    [InlineData(Outcome.Success)]
    [InlineData(Outcome.Failure)]
    [InlineData(Outcome.Cancelled)]
    public async Task WhenTheRecordInsertFails_TheStateWriteIsRolledBackToo_AndTheScopeStaysUsable(Outcome outcome)
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var startState = outcome == Outcome.Cancelled ? TaskState.Cancelling : TaskState.Executing;
        await SetRowAsync(seeded.TaskId, startState).ConfigureAwait(false);
        await RejectCompletionInsertsAsync(true).ConfigureAwait(false);

        try
        {
            await Run<IDeploymentCompletionHandler, IDeploymentDataProvider, IUnitOfWork>(async (handler, deployments, unitOfWork) =>
            {
                var deployment = await deployments.GetDeploymentByTaskIdAsync(seeded.TaskId, CancellationToken.None).ConfigureAwait(false);
                var ctx = new DeploymentTaskContext { ServerTaskId = seeded.TaskId, Deployment = deployment, LeaseOwner = Owner };

                await Should.ThrowAsync<Exception>(() => Drive(handler, outcome, ctx)).ConfigureAwait(false);

                // The failed insert must not stay tracked: the failure path and the checkpoint cleanup save on this same scoped context.
                await Should.NotThrowAsync(() => unitOfWork.SaveChangesAsync(CancellationToken.None)).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        finally
        {
            await RejectCompletionInsertsAsync(false).ConfigureAwait(false);
        }

        (await LoadTaskAsync(seeded.TaskId).ConfigureAwait(false)).State.ShouldBe(startState, "the terminal state must not be written without its completion record");
        (await CompletionCountAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBe(0);
    }

    [Theory]
    [InlineData(Outcome.Success, TaskState.Success)]
    [InlineData(Outcome.Failure, TaskState.Failed)]
    [InlineData(Outcome.Cancelled, TaskState.Cancelled)]
    public async Task WhenTheRunOwnsTheTask_StateAndRecordAreBothWritten(Outcome outcome, string expected)
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        await SetRowAsync(seeded.TaskId, outcome == Outcome.Cancelled ? TaskState.Cancelling : TaskState.Executing).ConfigureAwait(false);

        await DriveAsync(seeded.TaskId, outcome, Owner).ConfigureAwait(false);

        (await LoadTaskAsync(seeded.TaskId).ConfigureAwait(false)).State.ShouldBe(expected);
        (await CompletionCountAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBe(1);
    }

    [Theory]
    [InlineData(Outcome.Success)]
    [InlineData(Outcome.Failure)]
    [InlineData(Outcome.Cancelled)]
    public async Task WhenAnotherRunOwnsTheTask_NeitherStateNorRecordIsWritten(Outcome outcome)
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var startState = outcome == Outcome.Cancelled ? TaskState.Cancelling : TaskState.Executing;
        await SetRowAsync(seeded.TaskId, startState).ConfigureAwait(false);

        await DriveAsync(seeded.TaskId, outcome, "usurper:1:run").ConfigureAwait(false);

        (await LoadTaskAsync(seeded.TaskId).ConfigureAwait(false)).State.ShouldBe(startState);
        (await CompletionCountAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBe(0);
    }

    private static Task Drive(IDeploymentCompletionHandler handler, Outcome outcome, DeploymentTaskContext ctx) => outcome switch
    {
        Outcome.Success => handler.OnSuccessAsync(ctx, CancellationToken.None),
        Outcome.Failure => handler.OnFailureAsync(ctx, new InvalidOperationException("boom"), CancellationToken.None),
        _ => handler.OnCancelledAsync(ctx, CancellationToken.None)
    };

    private Task DriveAsync(int taskId, Outcome outcome, string runOwner)
        => Run<IDeploymentCompletionHandler, IDeploymentDataProvider>(async (handler, deployments) =>
        {
            var deployment = await deployments.GetDeploymentByTaskIdAsync(taskId, CancellationToken.None).ConfigureAwait(false);

            await Drive(handler, outcome, new DeploymentTaskContext { ServerTaskId = taskId, Deployment = deployment, LeaseOwner = runOwner }).ConfigureAwait(false);
        });

    private Task RejectCompletionInsertsAsync(bool reject)
        => Run<IRepository>(r => reject
            ? r.Database.ExecuteSqlRawAsync($"ALTER TABLE deployment_completion ADD CONSTRAINT {RejectAll} CHECK (false)")
            : r.Database.ExecuteSqlRawAsync($"ALTER TABLE deployment_completion DROP CONSTRAINT IF EXISTS {RejectAll}"));

    private Task SetRowAsync(int taskId, string state)
        => Run<IRepository>(r => r.ExecuteUpdateAsync<ServerTask>(t => t.Id == taskId, s => s.SetProperty(t => t.State, state).SetProperty(t => t.LeaseOwner, Owner).SetProperty(t => t.LeaseRenewedAt, t => DateTimeOffset.UtcNow), CancellationToken.None));

    private Task<ServerTask> LoadTaskAsync(int taskId) => Run<IServerTaskDataProvider, ServerTask>(p => p.GetServerTaskByIdNoTrackingAsync(taskId, CancellationToken.None));

    private Task<int> CompletionCountAsync(int taskId)
        => Run<IRepository, int>(async repository =>
        {
            var deploymentIds = await repository.QueryNoTracking<Deployment>(d => d.TaskId == taskId).Select(d => d.Id).ToListAsync().ConfigureAwait(false);

            return await repository.QueryNoTracking<DeploymentCompletion>(c => deploymentIds.Contains(c.DeploymentId)).CountAsync().ConfigureAwait(false);
        });
}
