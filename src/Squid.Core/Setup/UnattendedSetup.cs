using Squid.Core.Setup.Install;
using Squid.Core.Settings.Server;
using Squid.Core.Settings.Setup;
using Squid.Message.Models.Setup;

namespace Squid.Core.Setup;

/// <summary>
/// The setup request implied by start parameters alone (administrator password given, no database
/// connection string): built-in PostgreSQL, Redis as already configured, URLs as configured.
/// </summary>
public static class UnattendedSetup
{
    public const string DefaultAdminUserName = "admin";
    public const string DefaultExternalUrl = "http://localhost:8080";

    public static SetupInstallRequest BuildRequest(IConfiguration configuration) => new()
    {
        Database = new SetupDatabaseChoice { Mode = SetupServiceMode.Builtin },
        Redis = BuildRedisChoice(configuration),
        Admin = BuildAdmin(configuration),
        Server = BuildServerUrls(configuration)
    };

    private static SetupRedisChoice BuildRedisChoice(IConfiguration configuration)
    {
        if (new EmbeddedServicesSetting(configuration).Redis.Enabled) return new SetupRedisChoice { Mode = SetupServiceMode.Builtin };

        return new SetupRedisChoice { Mode = SetupServiceMode.External, ConnectionString = configuration[SetupInstaller.RedisConnectionStringKey] };
    }

    private static SetupAdminAccount BuildAdmin(IConfiguration configuration)
    {
        var admin = new BootstrapAdminSetting(configuration);

        return new SetupAdminAccount { UserName = string.IsNullOrWhiteSpace(admin.UserName) ? DefaultAdminUserName : admin.UserName, Password = admin.Password };
    }

    private static SetupServerUrls BuildServerUrls(IConfiguration configuration)
    {
        var urls = new ServerUrlSetting(configuration);

        if (string.IsNullOrWhiteSpace(urls.ExternalUrl))
            Log.Warning("No external URL configured; using {ExternalUrl}. Set SQUID_URL so agents and install scripts point at the right address", DefaultExternalUrl);

        return new SetupServerUrls { ExternalUrl = string.IsNullOrWhiteSpace(urls.ExternalUrl) ? DefaultExternalUrl : urls.ExternalUrl, CommsUrl = urls.CommsUrl };
    }
}
