using Squid.Calamari.Variables;

namespace Squid.Calamari.Pipeline;

public sealed class LoadVariablesFromFilesStep<TContext> : ExecutionStep<TContext>
    where TContext : IVariableLoadingExecutionContext
{
    public override Task ExecuteAsync(TContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        context.Variables = VariableSetFactory.CreateFromFiles(
            context.VariablesPath,
            context.SensitivePath,
            context.Password);

        return Task.CompletedTask;
    }
}
