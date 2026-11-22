using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Lease;

namespace Squid.Core.Services.DeploymentExecution.Pipeline.Phases;

public sealed class LoadTaskPhase(IServerTaskService serverTaskService, IServerTaskLeaseKeeper leaseKeeper) : IDeploymentPipelinePhase
{
    public int Order => 100;

    public async Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        var result = await serverTaskService.StartExecutingAsync(ctx.ServerTaskId, ct).ConfigureAwait(false);

        // Only a claim that succeeded (and fenced out a live owner) starts renewing; the runner releases it on every exit path.
        ctx.Lease = leaseKeeper.Start(ctx.ServerTaskId);

        ctx.Task = result.Task;
        ctx.IsResume = result.IsResumed;

        Log.Information("[Deploy] Start processing task {TaskId} (resume: {IsResume})", ctx.ServerTaskId, ctx.IsResume);
    }
}
