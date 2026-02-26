namespace Squid.Core.Settings.SelfCert;

public class SelfCertSetting : IConfigurationSetting
{
    public SelfCertSetting() { }

    public SelfCertSetting(IConfiguration configuration)
    {
        Base64 = configuration.GetValue<string>("SelfCert:Base64");
        Password = configuration.GetValue<string>("SelfCert:Password");
    }

    public string Base64 { get; set; }

    public string Password { get; set; }
}