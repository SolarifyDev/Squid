using Squid.Core.Setup.Configuration;
using Squid.Core.Setup.Install;
using Squid.Core.Setup.Secrets;
using Squid.Core.Settings.Setup;

namespace Squid.Core.Setup;

/// <summary>
/// Runs on every start of a self-contained install, before any host is built: generates the
/// per-install secrets that are still missing and, when no Redis is configured, switches on the
/// built-in one (when the image ships it). Everything it decides is written to the data directory's configuration file, so it
/// is decided once and stays stable across restarts and upgrades. No-op without a data directory.
/// </summary>
public static class DataDirectoryBootstrapper
{
    public static bool Run(IConfiguration configuration)
    {
        var dataDirectory = new DataDirectorySetting(configuration);

        if (!dataDirectory.IsConfigured) return false;

        WarnIfAnonymousVolume(dataDirectory);

        var values = new Dictionary<string, string>(InstallSecrets.GenerateMissing(configuration));

        AddBuiltinRedisIfUnconfigured(configuration, values);
        RememberConnectionStringsFromStartParameters(configuration, dataDirectory, values);

        SquidConfigurationFile.Merge(dataDirectory.ConfigurationFilePath, values);

        return values.Count > 0;
    }

    private static void WarnIfAnonymousVolume(DataDirectorySetting dataDirectory)
    {
        var warning = DataVolumeInspector.FindAnonymousVolumeWarning(dataDirectory.Value);

        if (warning != null) Log.Warning("{DataVolumeWarning}", warning);
    }

    /// <summary>
    /// A connection string passed as a start parameter (<c>SQUID_DB</c>, <c>SQUID_REDIS</c>) is
    /// copied into the data directory the first time it is seen, so re-creating the container for
    /// an upgrade without repeating the parameter still finds the same database instead of
    /// starting the setup over. A parameter given later still wins over the saved copy.
    /// </summary>
    private static void RememberConnectionStringsFromStartParameters(IConfiguration configuration, DataDirectorySetting dataDirectory, Dictionary<string, string> values)
    {
        foreach (var key in new[] { SetupInstaller.ConnectionStringKey, SetupInstaller.RedisConnectionStringKey })
        {
            var value = configuration[key];

            if (string.IsNullOrWhiteSpace(value) || values.ContainsKey(key)) continue;
            if (SquidConfigurationFile.GetValue(dataDirectory.ConfigurationFilePath, key) == value) continue;

            values[key] = value;
        }
    }

    private static void AddBuiltinRedisIfUnconfigured(IConfiguration configuration, Dictionary<string, string> values)
    {
        if (!string.IsNullOrWhiteSpace(configuration[SetupInstaller.RedisConnectionStringKey])) return;

        var redis = new EmbeddedServicesSetting(configuration).Redis;

        if (!Embedded.EmbeddedServiceHost.IsRedisAvailable(redis)) return;

        redis.Password = string.IsNullOrWhiteSpace(redis.Password) ? RandomSecret.Alphanumeric(32) : redis.Password;

        values["EmbeddedServices:Redis:Enabled"] = "true";
        values["EmbeddedServices:Redis:Password"] = redis.Password;
        values[SetupInstaller.RedisConnectionStringKey] = Embedded.EmbeddedRedis.BuildConnectionString(redis);
    }
}
