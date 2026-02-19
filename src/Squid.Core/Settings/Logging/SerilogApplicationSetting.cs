namespace Squid.Core.Settings.Logging;

public class SerilogApplicationSetting : IConfigurationSetting<string>
{
    public SerilogApplicationSetting(IConfiguration configuration)
    {
        Value = configuration.GetValue<string>("Serilog:Application");
    }

    public string Value { get; set; }
}
