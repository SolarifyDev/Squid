namespace Squid.Core.Settings.CorsPolicy;


public class AllowableCorsOriginsSetting(IConfiguration configuration) : IConfigurationSetting
{
    public string[] Value { get; set; } = (configuration["AllowableCorsOrigins"] ?? string.Empty)
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
