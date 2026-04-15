namespace Squid.Tentacle.ServiceHost;

/// <summary>
/// Placeholder macOS launchd implementation. Emits a clear "not yet implemented"
/// message so callers know to bail out. Fill this in (write a plist under
/// <c>/Library/LaunchDaemons</c>, use <c>launchctl load/unload</c>) when macOS
/// deployment targets become a real use-case.
/// </summary>
public sealed class LaunchdServiceHost : IServiceHost
{
    public string DisplayName => "launchd";

    public bool IsSupported => OperatingSystem.IsMacOS();

    public int Install(ServiceInstallRequest request) => NotImplemented(nameof(Install));
    public int Uninstall(string serviceName) => NotImplemented(nameof(Uninstall));
    public int Start(string serviceName) => NotImplemented(nameof(Start));
    public int Stop(string serviceName) => NotImplemented(nameof(Stop));
    public int Status(string serviceName) => NotImplemented(nameof(Status));

    private static int NotImplemented(string operation)
    {
        Console.Error.WriteLine(
            $"macOS launchd '{operation}' is not yet implemented for Squid Tentacle. " +
            "Use 'launchctl' with a plist under /Library/LaunchDaemons manually, " +
            "or track progress at https://github.com/SolarifyDev/Squid/issues.");
        return 2;
    }
}
