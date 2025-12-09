namespace Squid.Core.Settings.Security;

public class SecuritySetting : IConfigurationSetting
{
    public VariableEncryptionDto VariableEncryption { get; set; }
    
    // public string MasterKey { get; set; } = configuration["Security:VariableEncryption:MasterKey"];
}

public class VariableEncryptionDto
{
    public string MasterKey { get; set; }
}