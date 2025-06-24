using Microsoft.Extensions.Configuration;

namespace Squid.Core.Settings.SelfCert;

public class SelfCertSetting : IConfigurationSetting
{
    public SelfCertSetting(IConfiguration configuration)
    {
        Base64 = configuration["SelfCert:Base64"];
        
        Password = configuration["SelfCert:Password"];
    }
    
    public string Base64 { get; set; }
    
    public string Password { get; set; }
}