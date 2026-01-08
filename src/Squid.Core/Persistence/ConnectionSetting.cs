namespace Squid.Core.Persistence;

public class ConnectionSetting : IConfigurationSetting
{
    public string ConnectionString { get; set; }
    public string Version { get; set; }
}