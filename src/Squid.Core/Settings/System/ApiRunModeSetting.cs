using Squid.Message.Enums.System;

namespace Squid.Core.Settings.System;

public class ApiRunModeSetting : IConfigurationSetting<ApiRunMode>
{
    public ApiRunModeSetting(IConfiguration configuration)
    {
        Value = Enum.Parse<ApiRunMode>(configuration.GetValue<string>("ApiRunMode"), true);
    }
    
    public ApiRunMode Value { get; set; }
}