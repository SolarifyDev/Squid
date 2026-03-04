namespace Squid.Core.Settings.System;

public class SquidConnectionString : IConfigurationSetting<string>
{
    public SquidConnectionString(IConfiguration configuration)
    {
        Value = configuration.GetValue<string>("SquidStore:ConnectionString");
    }

    public string Value { get; set; }
}
