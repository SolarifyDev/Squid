namespace Squid.Core.Settings.GithubPackage;

public class CalamariGithubPackageSetting : IConfigurationSetting
{
    public string Version { get; set; }
    
    public string Token { get; set; }
}