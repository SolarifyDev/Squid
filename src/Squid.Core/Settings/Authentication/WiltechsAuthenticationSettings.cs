namespace Squid.Core.Settings.Authentication;

public class WiltechsAuthenticationSettings(IConfiguration configuration) : IConfigurationSetting
{
    public string BaseUrl { get; set; } = configuration.GetValue<string>("Authentication:Wiltechs:BaseUrl");
    
    public List<string> Issuers { get; set; } = configuration.GetValue<string>("Authentication:Wiltechs:Issuers")?.Split(",").ToList();

    public string Authority { get; set; } = configuration.GetValue<string>("Authentication:Wiltechs:Authority");
    
    public string SymmetricKey { get; set; } = configuration.GetValue<string>("Authentication:Wiltechs:SymmetricKey");
}