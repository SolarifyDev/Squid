using Squid.Core.Services.DeploymentExecution.Planning;

namespace Squid.Core.Services.DeploymentExecution.Pipeline.Phases;

/// <summary>
/// Builds a <see cref="DeploymentPlan"/> from the current <see cref="DeploymentTaskContext"/>
/// and stashes it on <see cref="DeploymentTaskContext.Plan"/> for consumption by later phases.
///
/// <para>
/// Runs at order 460 — after <see cref="PrepareTargetsPhase"/> (400) has resolved
/// <c>DeploymentTargetContext.Transport</c> for every candidate target, after
/// <see cref="AnnounceSetupPhase"/> (450) has emitted setup lifecycle events, and before
/// <see cref="ExecuteStepsPhase"/> (500) walks the steps.
/// </para>
///
/// <para>
/// Phase 6c-i (shadow mode): the planner is invoked in <see cref="PlanMode.Preview"/> so it
/// never throws on blockers. The plan is observed but not consumed — existing execute-time
/// filtering continues to be the source of truth for behavior. Phase 6c-iii will rewire
/// <see cref="ExecuteStepsPhase"/> to consume <c>ctx.Plan.Steps</c> directly and switch this
/// phase to <see cref="PlanMode.Execute"/> so blocker detection short-circuits the deployment
/// before any action runs.
/// </para>
///
/// <para>
/// Skipped entirely when <see cref="DeploymentTaskContext.IsServerOnlyDeployment"/> is set —
/// there are no target-level dispatches to plan in that case.
/// </para>
/// </summary>
public sealed class PlanDeploymentPhase(IDeploymentPlanner planner) : IDeploymentPipelinePhase
{
    public int Order => 460;

    public async Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        if (ShouldSkip(ctx))
            return;

        var request = BuildPlanRequest(ctx);

        ctx.Plan = await planner.PlanAsync(request, ct).ConfigureAwait(false);
    }

    private static bool ShouldSkip(DeploymentTaskContext ctx)
    {
        if (ctx.IsServerOnlyDeployment) return true;
        if (ctx.Steps == null || ctx.Steps.Count == 0) return true;

        return false;
    }

    private static DeploymentPlanRequest BuildPlanRequest(DeploymentTaskContext ctx) => new()
    {
        Mode = PlanMode.Preview,
        ReleaseId = ctx.Release?.Id ?? 0,
        EnvironmentId = ctx.Deployment?.EnvironmentId ?? 0,
        ChannelId = ctx.Deployment?.ChannelId ?? 0,
        DeploymentProcessSnapshotId = ctx.ProcessSnapshot?.Id ?? 0,
        Steps = ctx.Steps,
        Variables = ctx.Variables ?? new List<Message.Models.Deployments.Variable.VariableDto>(),
        TargetContexts = ctx.AllTargetsContext ?? new List<DeploymentTargetContext>(),
        DeploymentId = ctx.Deployment?.Id ?? 0,
        ServerTaskId = ctx.ServerTaskId
    };
}
