namespace Squid.Calamari.Pipeline;

public interface IExecutionStep<TContext>
{
    bool IsEnabled(TContext context);

    Task ExecuteAsync(TContext context, CancellationToken ct);
}
