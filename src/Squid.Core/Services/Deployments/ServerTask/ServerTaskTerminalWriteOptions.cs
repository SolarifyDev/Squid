using Squid.Core.Settings.ServerTask;

namespace Squid.Core.Services.Deployments.ServerTask;

/// <summary>
/// How a run retries the transaction that writes a task's terminal state and its completion record when the database fails transiently: the validated,
/// effective form of <see cref="ServerTaskTerminalWriteSettings"/> (configuration section ServerTask:TerminalWrite).
/// </summary>
public sealed class ServerTaskTerminalWriteOptions
{
    public const int DefaultMaxAttempts = 3;
    public const int DefaultRetryDelayMilliseconds = 200;
    public const int DefaultMaxRetryDelayMilliseconds = 5000;
    public const int DefaultMaxTotalRetryDelayMilliseconds = 15000;

    public int MaxAttempts { get; init; } = DefaultMaxAttempts;

    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(DefaultRetryDelayMilliseconds);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMilliseconds(DefaultMaxRetryDelayMilliseconds);

    public TimeSpan MaxTotalDelay { get; init; } = TimeSpan.FromMilliseconds(DefaultMaxTotalRetryDelayMilliseconds);

    public static ServerTaskTerminalWriteOptions From(ServerTaskSetting setting)
    {
        var maxAttempts = setting?.TerminalWrite?.MaxAttempts ?? DefaultMaxAttempts;
        var delayMilliseconds = setting?.TerminalWrite?.RetryDelayMilliseconds ?? DefaultRetryDelayMilliseconds;
        var maxDelayMilliseconds = setting?.TerminalWrite?.MaxRetryDelayMilliseconds ?? DefaultMaxRetryDelayMilliseconds;
        var maxTotalMilliseconds = setting?.TerminalWrite?.MaxTotalRetryDelayMilliseconds ?? DefaultMaxTotalRetryDelayMilliseconds;

        return new ServerTaskTerminalWriteOptions
        {
            MaxAttempts = maxAttempts > 0 ? maxAttempts : DefaultMaxAttempts,
            InitialDelay = TimeSpan.FromMilliseconds(delayMilliseconds >= 0 ? delayMilliseconds : DefaultRetryDelayMilliseconds),
            MaxDelay = TimeSpan.FromMilliseconds(maxDelayMilliseconds > 0 ? maxDelayMilliseconds : DefaultMaxRetryDelayMilliseconds),
            MaxTotalDelay = TimeSpan.FromMilliseconds(maxTotalMilliseconds > 0 ? maxTotalMilliseconds : DefaultMaxTotalRetryDelayMilliseconds)
        };
    }

    /// <summary>
    /// How long to wait before retrying after failed attempt <paramref name="attempt"/>, having already waited <paramref name="waited"/> for this write; null
    /// when no retry is left (the attempts or the total wait budget are used up). The wait grows threefold per attempt from <see cref="InitialDelay"/>, never
    /// beyond <see cref="MaxDelay"/>, and never past what remains of <see cref="MaxTotalDelay"/> — so a large attempt bound can neither stall a finished
    /// deployment for minutes nor overflow the delay.
    /// </summary>
    public TimeSpan? RetryDelay(int attempt, TimeSpan waited)
    {
        if (attempt >= MaxAttempts) return null;

        var remaining = MaxTotalDelay - waited;

        if (remaining <= TimeSpan.Zero) return null;

        var exponential = InitialDelay.TotalMilliseconds * Math.Pow(3, attempt - 1);
        var capped = Math.Min(Math.Min(exponential, MaxDelay.TotalMilliseconds), remaining.TotalMilliseconds);

        return TimeSpan.FromMilliseconds(capped);
    }
}
