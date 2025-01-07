namespace Squid.Infrastructure.Persistence.Postgres;

public class PostgresSetting : IConfigurationSetting
{
    public string ConnectionString { get; set; }
    public string Version { get; set; }
}