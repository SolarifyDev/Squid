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

    public int MaxAttempts { get; init; } = DefaultMaxAttempts;

    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(DefaultRetryDelayMilliseconds);

    public static ServerTaskTerminalWriteOptions From(ServerTaskSetting setting)
    {
        var maxAttempts = setting?.TerminalWrite?.MaxAttempts ?? DefaultMaxAttempts;
        var delayMilliseconds = setting?.TerminalWrite?.RetryDelayMilliseconds ?? DefaultRetryDelayMilliseconds;

        return new ServerTaskTerminalWriteOptions
        {
            MaxAttempts = maxAttempts > 0 ? maxAttempts : DefaultMaxAttempts,
            InitialDelay = TimeSpan.FromMilliseconds(delayMilliseconds >= 0 ? delayMilliseconds : DefaultRetryDelayMilliseconds)
        };
    }
}
