using Squid.Core.Services.Deployments.ServerTask;

namespace Squid.Core.Services.DeploymentExecution.Pipeline.Phases;

public sealed class LoadTaskPhase(IServerTaskService serverTaskService) : IDeploymentPipelinePhase
{
    public int Order => 100;

    public async Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        var task = await serverTaskService.StartExecutingAsync(ctx.ServerTaskId, ct).ConfigureAwait(false);

        ctx.Task = task;

        Log.Information("Start processing task {TaskId}", ctx.ServerTaskId);
    }
}
