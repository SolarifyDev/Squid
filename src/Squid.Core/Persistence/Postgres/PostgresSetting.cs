namespace Squid.Core.Persistence.Postgres;

public class PostgresSetting : IConfigurationSetting
{
    public string ConnectionString { get; set; }
    public string Version { get; set; }
}