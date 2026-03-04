namespace Squid.Calamari.Pipeline;

public sealed class ResolveWorkingDirectoryStep<TContext> : ExecutionStep<TContext>
    where TContext : IPathBasedExecutionContext
{
    public override Task ExecuteAsync(TContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        context.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(context.InputPath))
                                 ?? Directory.GetCurrentDirectory();

        return Task.CompletedTask;
    }
}
