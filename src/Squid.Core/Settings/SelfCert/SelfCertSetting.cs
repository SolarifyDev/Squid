namespace Squid.Core.Settings.SelfCert;

public class SelfCertSetting : IConfigurationSetting
{
    public string Base64 { get; set; }
    
    public string Password { get; set; }
}