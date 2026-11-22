using Squid.Core.Settings.ServerTask;

namespace Squid.Core.Services.Deployments.ServerTask.Cancel;

/// <summary>
/// How often the pod running a deployment checks whether a user cancelled it from another pod: the validated, effective form of
/// <see cref="ServerTaskCancelWatchSettings"/> (configuration section ServerTask:CancelWatch).
/// </summary>
public sealed class ServerTaskCancelWatchOptions
{
    public const int DefaultPollIntervalSeconds = 3;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(DefaultPollIntervalSeconds);

    public static ServerTaskCancelWatchOptions From(ServerTaskSetting setting)
    {
        var seconds = setting.CancelWatch?.PollIntervalSeconds ?? DefaultPollIntervalSeconds;

        return new ServerTaskCancelWatchOptions { PollInterval = TimeSpan.FromSeconds(seconds > 0 ? seconds : DefaultPollIntervalSeconds) };
    }
}
