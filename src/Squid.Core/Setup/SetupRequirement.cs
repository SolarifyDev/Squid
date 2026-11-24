using Squid.Core.Setup.Install;
using Squid.Core.Settings.Setup;

namespace Squid.Core.Setup;

/// <summary>
/// Decides how a start-up proceeds. First-run setup only ever applies to a self-contained install
/// (a data directory is configured) that has no database yet; it is unattended when the operator
/// already passed an administrator password, otherwise the setup wizard is served.
/// </summary>
public static class SetupRequirement
{
    public static bool IsRequired(IConfiguration configuration) => new DataDirectorySetting(configuration).IsConfigured && string.IsNullOrWhiteSpace(configuration[SetupInstaller.ConnectionStringKey]);

    public static bool IsUnattended(IConfiguration configuration) => IsRequired(configuration) && !string.IsNullOrWhiteSpace(new BootstrapAdminSetting(configuration).Password);
}
