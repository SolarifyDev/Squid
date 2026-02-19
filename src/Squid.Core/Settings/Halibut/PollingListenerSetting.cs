namespace Squid.Core.Settings.Halibut;

public class PollingListenerSetting : IConfigurationSetting
{
    public int Port { get; set; } = 10943;
    public bool Enabled { get; set; } = true;
}
