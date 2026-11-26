namespace Squid.Core.Settings.ServerTask;

public class ServerTaskSetting : IConfigurationSetting
{
    public ServerTaskSetting() { }

    public ServerTaskSetting(IConfiguration configuration)
    {
        var section = configuration.GetSection("ServerTask");

        Lease = section.GetSection("Lease").Get<ServerTaskLeaseSettings>() ?? new ServerTaskLeaseSettings();
        CancelWatch = section.GetSection("CancelWatch").Get<ServerTaskCancelWatchSettings>() ?? new ServerTaskCancelWatchSettings();
        TerminalWrite = section.GetSection("TerminalWrite").Get<ServerTaskTerminalWriteSettings>() ?? new ServerTaskTerminalWriteSettings();
    }

    public ServerTaskLeaseSettings Lease { get; set; } = new();

    public ServerTaskCancelWatchSettings CancelWatch { get; set; } = new();

    public ServerTaskTerminalWriteSettings TerminalWrite { get; set; } = new();
}

public class ServerTaskTerminalWriteSettings
{
    /// <summary>How many times the transaction that writes a task's terminal state and its DeploymentCompletion is tried when the database fails transiently (a dropped connection, a serialization failure) before the failure is surfaced. Non-positive values fall back to 3.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>The wait before the first retry; each further retry waits three times as long. Negative values fall back to 200.</summary>
    public int RetryDelayMilliseconds { get; set; } = 200;

    /// <summary>The longest any single retry waits, however many attempts are allowed. Non-positive values fall back to 5000.</summary>
    public int MaxRetryDelayMilliseconds { get; set; } = 5000;

    /// <summary>The most all retries of one write wait in total; once it is used up the failure is surfaced even if attempts remain. Non-positive values fall back to 15000.</summary>
    public int MaxTotalRetryDelayMilliseconds { get; set; } = 15000;
}

public class ServerTaskLeaseSettings
{
    /// <summary>How often a running task renews its lease. Floored at 5 seconds when read.</summary>
    public int RenewIntervalSeconds { get; set; } = 20;

    /// <summary>How long a lease may go unrenewed before the reaper recovers the task. Never below three renewal intervals when read.</summary>
    public int StaleSeconds { get; set; } = 300;

    /// <summary>How many times the reaper resumes a task on its own after recovering it from a stopped server; past that it stays Paused for an operator (a deployment that keeps killing its server must not loop). 0 turns automatic resume off; a negative value falls back to the default of 3.</summary>
    public int MaxAutoResumes { get; set; } = 3;
}

public class ServerTaskCancelWatchSettings
{
    /// <summary>How often the pod running a deployment checks whether a user cancelled it from another pod. Non-positive values fall back to 3 seconds.</summary>
    public int PollIntervalSeconds { get; set; } = 3;
}
