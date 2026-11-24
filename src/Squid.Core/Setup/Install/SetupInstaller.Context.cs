using Squid.Core.Settings.Setup;
using Squid.Message.Models.Setup;

namespace Squid.Core.Setup.Install;

public sealed partial class SetupInstaller
{
    private SetupInstallContext BuildContext(SetupInstallRequest request) => new()
    {
        Request = request,
        DataDirectory = new DataDirectorySetting(_configuration),
        Embedded = new EmbeddedServicesSetting(_configuration)
    };

    private sealed class SetupInstallContext
    {
        // Input
        public SetupInstallRequest Request { get; init; }
        public DataDirectorySetting DataDirectory { get; init; }
        public EmbeddedServicesSetting Embedded { get; init; }

        // Resolved
        public string DatabaseConnectionString { get; set; }
        public string RedisConnectionString { get; set; }
        public Dictionary<string, string> ConfigurationToPersist { get; } = new();
    }
}
