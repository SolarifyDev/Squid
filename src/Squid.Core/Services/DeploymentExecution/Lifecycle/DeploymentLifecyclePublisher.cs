namespace Squid.Core.Services.DeploymentExecution.Lifecycle;

public sealed class DeploymentLifecyclePublisher : IDeploymentLifecycle
{
    private List<IDeploymentLifecycleHandler> _ordered;
    private readonly IEnumerable<IDeploymentLifecycleHandler> _handlers;

    public DeploymentLifecyclePublisher(IEnumerable<IDeploymentLifecycleHandler> handlers) => _handlers = handlers;

    public void Initialize(DeploymentTaskContext ctx)
    {
        _ordered = _handlers.OrderBy(h => h.Order).ToList();

        foreach (var handler in _ordered)
            handler.Initialize(ctx);
    }

    public async Task EmitAsync(DeploymentLifecycleEvent @event, CancellationToken ct)
    {
        foreach (var handler in _ordered)
            await handler.HandleAsync(@event, ct).ConfigureAwait(false);
    }
}
