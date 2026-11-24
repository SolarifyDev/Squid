namespace Squid.Core.Settings.Setup;

/// <summary>
/// Built-in PostgreSQL / Redis that the Squid process starts itself (all-in-one image). Written
/// by the first-run setup into the data directory's configuration file, never hand-edited in
/// normal use.
/// </summary>
public class EmbeddedServicesSetting : IConfigurationSetting
{
    public const string SectionName = "EmbeddedServices";

    public EmbeddedServicesSetting() { }

    public EmbeddedServicesSetting(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);

        Postgres = section.GetSection("Postgres").Get<EmbeddedPostgresSettings>() ?? new EmbeddedPostgresSettings();
        Redis = section.GetSection("Redis").Get<EmbeddedRedisSettings>() ?? new EmbeddedRedisSettings();
    }

    public EmbeddedPostgresSettings Postgres { get; set; } = new();
    public EmbeddedRedisSettings Redis { get; set; } = new();
}

public class EmbeddedPostgresSettings
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "squid";
    public string UserName { get; set; } = "squid";
    public string Password { get; set; } = string.Empty;

    /// <summary>Directory holding <c>initdb</c> / <c>pg_ctl</c>; empty = discover.</summary>
    public string BinDirectory { get; set; } = string.Empty;
}

public class EmbeddedRedisSettings
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 6379;
    public string Password { get; set; } = string.Empty;

    /// <summary>Path to <c>redis-server</c>; empty = discover on PATH.</summary>
    public string ServerPath { get; set; } = string.Empty;
}
