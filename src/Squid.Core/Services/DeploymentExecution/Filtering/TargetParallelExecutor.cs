using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Filtering;

public static class TargetParallelExecutor
{
    public static async Task<List<TResult>> ExecuteAsync<TItem, TResult>(
        List<TItem> items, int maxParallelism, Func<TItem, CancellationToken, Task<TResult>> executeAsync, CancellationToken ct)
    {
        if (items.Count == 0)
            return new List<TResult>();

        if (items.Count == 1 || maxParallelism == 1)
            return await ExecuteSequentialAsync(items, executeAsync, ct).ConfigureAwait(false);

        if (maxParallelism <= 0 || maxParallelism >= items.Count)
            return await ExecuteAllConcurrentAsync(items, executeAsync, ct).ConfigureAwait(false);

        return await ExecuteThrottledAsync(items, maxParallelism, executeAsync, ct).ConfigureAwait(false);
    }

    public static int ParseMaxParallelism(DeploymentStepDto step)
    {
        var property = step.Properties?.FirstOrDefault(p => p.PropertyName == SpecialVariables.Step.MaxParallelism);

        if (property == null || string.IsNullOrEmpty(property.PropertyValue))
            return 0;

        return int.TryParse(property.PropertyValue, out var value) && value > 0 ? value : 0;
    }

    private static async Task<List<TResult>> ExecuteSequentialAsync<TItem, TResult>(
        List<TItem> items, Func<TItem, CancellationToken, Task<TResult>> executeAsync, CancellationToken ct)
    {
        var results = new List<TResult>(items.Count);

        foreach (var item in items)
            results.Add(await executeAsync(item, ct).ConfigureAwait(false));

        return results;
    }

    private static async Task<List<TResult>> ExecuteAllConcurrentAsync<TItem, TResult>(
        List<TItem> items, Func<TItem, CancellationToken, Task<TResult>> executeAsync, CancellationToken ct)
    {
        var tasks = items.Select(item => executeAsync(item, ct)).ToArray();

        await AwaitAllCollectingFailuresAsync(tasks, ct).ConfigureAwait(false);

        return tasks.Select(t => t.Result).ToList();
    }

    private static async Task<List<TResult>> ExecuteThrottledAsync<TItem, TResult>(
        List<TItem> items, int maxParallelism, Func<TItem, CancellationToken, Task<TResult>> executeAsync, CancellationToken ct)
    {
        var results = new TResult[items.Count];
        using var semaphore = new SemaphoreSlim(maxParallelism);

        var tasks = items.Select(async (item, index) =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                results[index] = await executeAsync(item, ct).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        await AwaitAllCollectingFailuresAsync(tasks, ct).ConfigureAwait(false);

        return results.ToList();
    }

    /// <summary>
    /// P0-A.2 (2026-04-24 audit): <c>await Task.WhenAll(tasks)</c> rethrows only the
    /// FIRST exception — every other faulted task is marked "observed" and silently
    /// discarded. In a multi-target deployment this means only one target's failure
    /// reached the operator; the others were hidden, driving fix-one / redeploy /
    /// fix-next cycles.
    ///
    /// <para>This helper awaits <c>Task.WhenAll</c> first (so the happy path is
    /// unchanged), then walks every faulted task to collect every
    /// <c>Exception.InnerExceptions</c> entry into a single
    /// <see cref="AggregateException"/>. If the only "failure" across all tasks is
    /// cancellation, we propagate a clean <see cref="OperationCanceledException"/>
    /// — callers that catch OCE specifically for "user-cancelled deploy" keep
    /// working.</para>
    /// </summary>
    private static async Task AwaitAllCollectingFailuresAsync(Task[] tasks, CancellationToken ct)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
            return;
        }
        catch
        {
            // Fall through to the collection branch below — we need every faulted
            // task's exception, not just the first one Task.WhenAll chose to rethrow.
        }

        var faultedExceptions = tasks
            .Where(t => t.IsFaulted && t.Exception != null)
            .SelectMany(t => t.Exception!.InnerExceptions)
            .ToList();

        if (faultedExceptions.Count == 1)
        {
            // Preserve the original exception type for the single-failure case so
            // downstream catch blocks (DeploymentPipelineRunner's error-classification,
            // retry policy) keep matching on the specific type. Only multi-failure
            // paths aggregate — that's the actual P0-A.2 vector (silent drops).
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(faultedExceptions[0]).Throw();
        }

        if (faultedExceptions.Count > 0)
        {
            throw new AggregateException(
                $"{faultedExceptions.Count} of {tasks.Length} parallel tasks failed",
                faultedExceptions);
        }

        // No faulted tasks — everything that "failed" was cancellation. Re-throw a
        // clean OCE so callers that catch OperationCanceledException explicitly still
        // match. Prefer the caller's ct for the message, fall back to a generic OCE.
        ct.ThrowIfCancellationRequested();
        throw new OperationCanceledException();
    }
}
