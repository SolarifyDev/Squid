using Squid.Core.Services.DeploymentExecution.Lifecycle;

namespace Squid.Core.Services.DeploymentExecution.Pipeline.Phases;

public sealed class AnnounceSetupPhase(IDeploymentLifecycle lifecycle) : IDeploymentPipelinePhase
{
    public int Order => 450;

    public async Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        if (ctx.IsResume)
            await AnnounceResumeAsync(ctx, ct).ConfigureAwait(false);
        else
            await AnnounceFreshDeployAsync(ctx, ct).ConfigureAwait(false);

        await AnnounceExcludedTargetsAsync(ctx, ct).ConfigureAwait(false);
    }

    private async Task AnnounceFreshDeployAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        await lifecycle.EmitAsync(new DeploymentStartingEvent(new DeploymentEventContext()), ct).ConfigureAwait(false);
        await lifecycle.EmitAsync(new MachineConstraintsResolvedEvent(new DeploymentEventContext { Targets = ctx.AllTargets }), ct).ConfigureAwait(false);
        await lifecycle.EmitAsync(new TargetsResolvedEvent(new DeploymentEventContext { Targets = ctx.AllTargets }), ct).ConfigureAwait(false);

        foreach (var tc in ctx.AllTargetsContext)
        {
            await lifecycle.EmitAsync(new TargetPreparingEvent(new DeploymentEventContext { MachineName = tc.Machine.Name, CommunicationStyle = tc.CommunicationStyle }), ct).ConfigureAwait(false);

            if (tc.Transport == null)
                await lifecycle.EmitAsync(new TargetTransportMissingEvent(new DeploymentEventContext { MachineName = tc.Machine.Name, CommunicationStyle = tc.CommunicationStyle }), ct).ConfigureAwait(false);
        }
    }

    private async Task AnnounceResumeAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        await lifecycle.EmitAsync(new DeploymentResumingEvent(new DeploymentEventContext()), ct).ConfigureAwait(false);
    }

    private async Task AnnounceExcludedTargetsAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        if (ctx.ExcludedByHealthTargets?.Count > 0)
            await lifecycle.EmitAsync(new UnhealthyTargetsExcludedEvent(new DeploymentEventContext { Targets = ctx.ExcludedByHealthTargets }), ct).ConfigureAwait(false);
    }
}
