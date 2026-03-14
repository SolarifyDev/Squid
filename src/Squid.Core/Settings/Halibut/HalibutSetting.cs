namespace Squid.Core.Settings.Halibut;

public class HalibutSetting : IConfigurationSetting
{
    public HalibutSetting() { }

    public HalibutSetting(IConfiguration configuration)
    {
        var section = configuration.GetSection("Halibut");
        
        Polling = section.GetSection("Polling").Get<PollingSettings>() ?? new PollingSettings();
    }

    public PollingSettings Polling { get; set; } = new();
}

public class PollingSettings
{
    public int Port { get; set; } = 10943;
    public bool Enabled { get; set; }
}
