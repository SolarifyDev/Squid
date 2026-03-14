using Squid.Core.Services.Deployments.ServerTask;

namespace Squid.Core.Services.DeploymentExecution.Pipeline.Phases;

public sealed class LoadTaskPhase(IServerTaskService serverTaskService) : IDeploymentPipelinePhase
{
    public int Order => 100;

    public async Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        var result = await serverTaskService.StartExecutingAsync(ctx.ServerTaskId, ct).ConfigureAwait(false);

        ctx.Task = result.Task;
        ctx.IsResume = result.IsResumed;

        Log.Information("Start processing task {TaskId} (resume: {IsResume})", ctx.ServerTaskId, ctx.IsResume);
    }
}
