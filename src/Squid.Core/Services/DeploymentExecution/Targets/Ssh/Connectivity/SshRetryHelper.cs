using Serilog;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

public static class SshRetryHelper
{
    internal const int DefaultMaxAttempts = 3;
    internal static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromSeconds(15);

    public static T ExecuteWithRetry<T>(Func<T> action, Func<Exception, bool> isTransient, int maxAttempts = DefaultMaxAttempts)
    {
        var delay = DefaultInitialDelay;
        Exception lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (attempt < maxAttempts && isTransient(ex))
            {
                lastException = ex;

                Log.Warning("[SSH] Transient error on attempt {Attempt}/{MaxAttempts}, retrying in {Delay}ms: {Error}", attempt, maxAttempts, delay.TotalMilliseconds, ex.Message);

                Thread.Sleep(delay);
                delay = NextDelay(delay);
            }
        }

        throw lastException!;
    }

    public static void ExecuteWithRetry(Action action, Func<Exception, bool> isTransient, int maxAttempts = DefaultMaxAttempts)
    {
        ExecuteWithRetry(() => { action(); return true; }, isTransient, maxAttempts);
    }

    public static async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action, Func<Exception, bool> isTransient, int maxAttempts = DefaultMaxAttempts, CancellationToken ct = default)
    {
        var delay = DefaultInitialDelay;
        Exception lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxAttempts && isTransient(ex))
            {
                lastException = ex;

                Log.Warning("[SSH] Transient error on attempt {Attempt}/{MaxAttempts}, retrying in {Delay}ms: {Error}", attempt, maxAttempts, delay.TotalMilliseconds, ex.Message);

                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = NextDelay(delay);
            }
        }

        throw lastException!;
    }

    internal static TimeSpan NextDelay(TimeSpan current)
    {
        var next = TimeSpan.FromTicks(current.Ticks * 2);

        return next > DefaultMaxDelay ? DefaultMaxDelay : next;
    }
}
