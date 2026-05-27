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
            // Surface the failure to opt-in failure-aware contexts so cleanup-
            // phase steps (DeployFailed convention etc.) can fire conditionally.
            // SourceException is still preserved for re-throw at end of pipeline —
            // this flag is an additional signal, not a substitution.
            if (context is IFailureAwareExecutionContext failureAware)
                failureAware.ExecutionFailed = true;

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
