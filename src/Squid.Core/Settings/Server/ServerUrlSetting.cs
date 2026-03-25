namespace Squid.Core.Settings.Server;

public class ServerUrlSetting : IConfigurationSetting
{
    public ServerUrlSetting() { }

    public ServerUrlSetting(IConfiguration configuration)
    {
        ExternalUrl = configuration.GetValue<string>("ServerUrl:ExternalUrl") ?? string.Empty;
        CommsUrl = configuration.GetValue<string>("ServerUrl:CommsUrl") ?? string.Empty;
    }

    public string ExternalUrl { get; set; } = string.Empty;
    public string CommsUrl { get; set; } = string.Empty;
}
