namespace Squid.Core.Services.Deployments.ServerTask.Cancel;

/// <summary>
/// How often the pod running a deployment checks whether a user cancelled it from another pod. Operators tune it with the env var
/// below; the name is pinned by a test because renaming it silently drops an operator's override.
/// </summary>
public sealed class ServerTaskCancelWatchOptions
{
    public const string PollIntervalSecondsEnvVar = "SQUID_SERVER_TASK_CANCEL_POLL_INTERVAL_SECONDS";

    public const int DefaultPollIntervalSeconds = 3;
    public const int MinPollIntervalSeconds = 1;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(DefaultPollIntervalSeconds);

    public static ServerTaskCancelWatchOptions FromEnvironment() => From(Environment.GetEnvironmentVariable);

    public static ServerTaskCancelWatchOptions From(Func<string, string> read)
    {
        var seconds = int.TryParse(read(PollIntervalSecondsEnvVar), out var value) && value >= MinPollIntervalSeconds ? value : DefaultPollIntervalSeconds;

        return new ServerTaskCancelWatchOptions { PollInterval = TimeSpan.FromSeconds(seconds) };
    }
}
