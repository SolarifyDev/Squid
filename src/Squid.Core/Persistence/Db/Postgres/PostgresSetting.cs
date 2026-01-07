namespace Squid.Core.Persistence.Db.Postgres;

public class PostgresSetting : IConfigurationSetting
{
    public string ConnectionString { get; set; }
    public string Version { get; set; }
}