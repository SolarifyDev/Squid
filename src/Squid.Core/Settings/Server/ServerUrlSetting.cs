namespace Squid.Core.Settings.Server;

public class ServerUrlSetting : IConfigurationSetting
{
    public string ExternalUrl { get; set; } = string.Empty;
    public string CommsUrl { get; set; } = string.Empty;
}
