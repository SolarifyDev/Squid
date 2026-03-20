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

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        return results.ToList();
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

        await Task.WhenAll(tasks).ConfigureAwait(false);

        return results.ToList();
    }
}
