using Squid.Core.Services.DeploymentExecution.Lifecycle;

namespace Squid.Core.Services.DeploymentExecution.Pipeline;

public sealed class DeploymentPipelineRunner : IDeploymentTaskExecutor
{
    private readonly IEnumerable<IDeploymentPipelinePhase> _phases;
    private readonly IDeploymentLifecycle _lifecycle;
    private readonly IDeploymentCompletionHandler _completion;

    public DeploymentPipelineRunner(IEnumerable<IDeploymentPipelinePhase> phases, IDeploymentLifecycle lifecycle, IDeploymentCompletionHandler completion)
    {
        _phases = phases;
        _lifecycle = lifecycle;
        _completion = completion;
    }

    public async Task ProcessAsync(int serverTaskId, CancellationToken ct)
    {
        var ctx = new DeploymentTaskContext { ServerTaskId = serverTaskId };
        
        _lifecycle.Initialize(ctx);

        var ordered = _phases.OrderBy(p => p.Order).ToList();

        try
        {
            foreach (var phase in ordered)
                await phase.ExecuteAsync(ctx, ct);

            await _lifecycle.EmitAsync(new DeploymentSucceededEvent(new DeploymentEventContext()), ct);
            await _completion.OnSuccessAsync(ctx, ct);
        }
        catch (Exception ex)
        {
            await _lifecycle.EmitAsync(new DeploymentFailedEvent(new DeploymentEventContext { Exception = ex }), ct);
            await _completion.OnFailureAsync(ctx, ex, ct);
            throw;
        }
    }
}
