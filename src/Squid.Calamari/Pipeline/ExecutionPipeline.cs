using System.Runtime.ExceptionServices;

namespace Squid.Calamari.Pipeline;

public sealed class ExecutionPipeline<TContext>
{
    private readonly IReadOnlyList<IExecutionStep<TContext>> _steps;

    public ExecutionPipeline(IEnumerable<IExecutionStep<TContext>> steps)
    {
        _steps = steps?.ToArray() ?? Array.Empty<IExecutionStep<TContext>>();
    }

    public async Task ExecuteAsync(TContext context, CancellationToken ct)
    {
        ExceptionDispatchInfo? executionFailure = null;

        try
        {
            foreach (var step in _steps)
            {
                if (step is IAlwaysRunExecutionStep<TContext>)
                    continue;

                ct.ThrowIfCancellationRequested();

                if (!step.IsEnabled(context))
                    continue;

                await step.ExecuteAsync(context, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            executionFailure = ExceptionDispatchInfo.Capture(ex);
        }

        ExceptionDispatchInfo? cleanupFailure = null;

        foreach (var step in _steps)
        {
            if (step is not IAlwaysRunExecutionStep<TContext>)
                continue;

            try
            {
                ct.ThrowIfCancellationRequested();

                if (!step.IsEnabled(context))
                    continue;

                await step.ExecuteAsync(context, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (cleanupFailure is null)
            {
                cleanupFailure = ExceptionDispatchInfo.Capture(ex);
            }
            catch
            {
                // Preserve the first cleanup failure to avoid hiding the original execution error.
            }
        }

        if (executionFailure is not null && cleanupFailure is not null)
            throw new AggregateException(executionFailure.SourceException, cleanupFailure.SourceException);

        cleanupFailure?.Throw();
        executionFailure?.Throw();
    }
}
