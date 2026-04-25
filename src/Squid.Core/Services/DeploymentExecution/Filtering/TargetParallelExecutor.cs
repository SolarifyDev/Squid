using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Filtering;

public static class TargetParallelExecutor
{
    /// <summary>
    /// Phase-6.3: env var that selects the process-wide parallelism cap. The
    /// cap is acquired by every parallel task in <see cref="ExecuteAsync"/>
    /// regardless of per-step <paramref name="maxParallelism"/> — closes the
    /// "fan-out × fan-out" amplification where two parallel steps each run at
    /// their own MaxParallelism and the totals add up to N×M concurrent
    /// Halibut connections.
    ///
    /// <para>Recognised values (case-insensitive, trimmed):</para>
    /// <list type="bullet">
    ///   <item>(unset / blank / <c>auto</c>) → <c>ProcessorCount × 4</c>
    ///         (sensible default for typical 8-16 vCPU server pods).</item>
    ///   <item>integer literal → that exact cap.</item>
    ///   <item><c>disabled</c> / <c>0</c> → no global cap (legacy behaviour;
    ///         dev / tests / explicit operator opt-out).</item>
    ///   <item>unrecognised → fall back to auto (typo'd env var must not
    ///         crash the process).</item>
    /// </list>
    /// </summary>
    public const string GlobalParallelismCapEnvVar = "SQUID_TARGET_PARALLELISM_GLOBAL_CAP";

    private const int DefaultGlobalCapMultiplier = 4;

    private static readonly Lazy<SemaphoreSlim> _globalCap = new(BuildGlobalCap, isThreadSafe: true);

    private static SemaphoreSlim _testOverrideCap;
    private static bool _testOverrideActive;

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

    /// <summary>
    /// Returns the effective global cap (or null if disabled). Consult this
    /// directly only at boundaries where you want to short-circuit on
    /// disabled mode; production code paths use the semaphore via
    /// <see cref="ExecuteAsync"/>.
    /// </summary>
    private static SemaphoreSlim GetEffectiveGlobalCap()
        => _testOverrideActive ? _testOverrideCap : _globalCap.Value;

    /// <summary>
    /// Pure parser exposed for unit testing. Returns null for disabled-mode
    /// env values (<c>"disabled"</c> / <c>"0"</c>); a positive int otherwise.
    /// </summary>
    internal static int? ParseGlobalCap(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Environment.ProcessorCount * DefaultGlobalCapMultiplier;

        var trimmed = raw.Trim();

        if (string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase))
            return Environment.ProcessorCount * DefaultGlobalCapMultiplier;

        if (string.Equals(trimmed, "disabled", StringComparison.OrdinalIgnoreCase))
            return null;

        if (int.TryParse(trimmed, out var explicitCap))
        {
            if (explicitCap <= 0) return null;
            return explicitCap;
        }

        return Environment.ProcessorCount * DefaultGlobalCapMultiplier;
    }

    private static SemaphoreSlim BuildGlobalCap()
    {
        var raw = Environment.GetEnvironmentVariable(GlobalParallelismCapEnvVar);
        var cap = ParseGlobalCap(raw);
        return cap.HasValue ? new SemaphoreSlim(cap.Value, cap.Value) : null;
    }

    /// <summary>
    /// Test seam: temporarily swap the process-wide cap for one suite. Always
    /// pair with <see cref="ResetGlobalCapForTesting"/> in a finally so other
    /// tests in the same process see the restored default.
    /// </summary>
    internal static void SetGlobalCapForTesting(int? cap)
    {
        _testOverrideCap = cap.HasValue ? new SemaphoreSlim(cap.Value, cap.Value) : null;
        _testOverrideActive = true;
    }

    internal static void ResetGlobalCapForTesting()
    {
        _testOverrideCap?.Dispose();
        _testOverrideCap = null;
        _testOverrideActive = false;
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
        var globalCap = GetEffectiveGlobalCap();

        var tasks = items.Select(item => RunWithGlobalCapAsync(globalCap, () => executeAsync(item, ct), ct)).ToArray();

        await AwaitAllCollectingFailuresAsync(tasks, ct).ConfigureAwait(false);

        return tasks.Select(t => t.Result).ToList();
    }

    private static async Task<List<TResult>> ExecuteThrottledAsync<TItem, TResult>(
        List<TItem> items, int maxParallelism, Func<TItem, CancellationToken, Task<TResult>> executeAsync, CancellationToken ct)
    {
        var globalCap = GetEffectiveGlobalCap();
        var results = new TResult[items.Count];
        using var perStep = new SemaphoreSlim(maxParallelism);

        var tasks = items.Select(async (item, index) =>
        {
            await perStep.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                results[index] = await RunWithGlobalCapAsync(globalCap, () => executeAsync(item, ct), ct).ConfigureAwait(false);
            }
            finally
            {
                perStep.Release();
            }
        }).ToArray();

        await AwaitAllCollectingFailuresAsync(tasks, ct).ConfigureAwait(false);

        return results.ToList();
    }

    /// <summary>
    /// Acquires the process-wide cap (if active) before running the worker.
    /// Always releases on the way out — even if the worker throws — so a
    /// faulted task does not permanently consume a slot.
    /// </summary>
    private static async Task<TResult> RunWithGlobalCapAsync<TResult>(
        SemaphoreSlim globalCap, Func<Task<TResult>> work, CancellationToken ct)
    {
        if (globalCap == null)
            return await work().ConfigureAwait(false);

        await globalCap.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            globalCap.Release();
        }
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
