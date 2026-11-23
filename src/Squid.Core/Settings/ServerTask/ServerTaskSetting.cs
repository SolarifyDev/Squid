namespace Squid.Core.Settings.ServerTask;

public class ServerTaskSetting : IConfigurationSetting
{
    public ServerTaskSetting() { }

    public ServerTaskSetting(IConfiguration configuration)
    {
        var section = configuration.GetSection("ServerTask");

        Lease = section.GetSection("Lease").Get<ServerTaskLeaseSettings>() ?? new ServerTaskLeaseSettings();
    }

    public ServerTaskLeaseSettings Lease { get; set; } = new();
}

public class ServerTaskLeaseSettings
{
    /// <summary>How often a running task renews its lease. Floored at 5 seconds when read.</summary>
    public int RenewIntervalSeconds { get; set; } = 20;

    /// <summary>How long a lease may go unrenewed before the reaper recovers the task. Never below three renewal intervals when read.</summary>
    public int StaleSeconds { get; set; } = 300;
}
