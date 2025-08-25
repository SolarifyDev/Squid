namespace Squid.Core.Settings.Security;

public class SecuritySetting(IConfiguration configuration) : IConfigurationSetting
{
    public string MasterKey { get; set; } = configuration["Security:VariableEncryption:MasterKey"];
}