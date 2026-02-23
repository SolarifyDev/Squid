namespace Squid.Calamari.Pipeline;

public abstract class ExecutionStep<TContext> : IExecutionStep<TContext>
{
    public virtual bool IsEnabled(TContext context) => true;

    public abstract Task ExecuteAsync(TContext context, CancellationToken ct);
}
