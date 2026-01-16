using Squid.Message.Enums.System;

namespace Squid.Core.Settings.System;

public class HangfireHostingSetting : IConfigurationSetting<HangfireHosting>
{
    public HangfireHostingSetting(IConfiguration configuration)
    {
        Value = Enum.Parse<HangfireHosting>(configuration.GetValue<string>("HangfireHosting"), true);
    }
    
    public HangfireHosting Value { get; set; }
}