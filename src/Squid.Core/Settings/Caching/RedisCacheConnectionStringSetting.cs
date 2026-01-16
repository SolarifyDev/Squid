namespace Squid.Core.Settings.Caching;

public class RedisCacheConnectionStringSetting : IConfigurationSetting<string>
{
    public RedisCacheConnectionStringSetting(IConfiguration configuration)
    {
        Value = configuration.GetValue<string>("RedisCacheConnectionString");
    }
    
    public string Value { get; set; }
}