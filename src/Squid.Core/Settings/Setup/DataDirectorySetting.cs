namespace Squid.Core.Settings.Setup;

/// <summary>
/// Writable directory that owns a self-contained install (the all-in-one Docker image sets it to
/// <c>/data</c>). When set, Squid keeps its own generated configuration in
/// <see cref="ConfigurationFileName"/> there, runs the first-run setup when no database is
/// configured, and keeps built-in PostgreSQL / Redis data under it. When empty — every
/// pre-existing deployment — none of that happens and configuration comes only from
/// appsettings / environment as before.
/// </summary>
public class DataDirectorySetting : IConfigurationSetting<string>
{
    public const string Key = "DataDirectory";

    public const string ConfigurationFileName = "squid.json";

    public DataDirectorySetting(IConfiguration configuration)
    {
        Value = configuration.GetValue<string>(Key) ?? string.Empty;
    }

    public string Value { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Value);

    public string ConfigurationFilePath => Path.Combine(Value, ConfigurationFileName);

    public string PathOf(string relative) => Path.Combine(Value, relative);
}
