using Squid.Core.Services.Deployments.LifeCycle;

namespace Squid.Core.Services.DeploymentExecution.Pipeline.Phases;

public sealed class RetentionCleanupPhase(IRetentionPolicyEnforcer retentionEnforcer) : IDeploymentPipelinePhase
{
    public int Order => 600;

    public async Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        if (ctx.Project == null) return;

        try
        {
            await retentionEnforcer.EnforceRetentionForProjectAsync(ctx.Project.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Retention cleanup failed for project {ProjectId}, continuing", ctx.Project.Id);
        }
    }
}
