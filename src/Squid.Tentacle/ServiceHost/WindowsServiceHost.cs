namespace Squid.Tentacle.ServiceHost;

/// <summary>
/// Placeholder Windows Service implementation. Emits a clear "not yet implemented"
/// message so callers (ServiceCommand) know to bail out gracefully rather than
/// pretending to succeed. Fill this in when Windows Tentacle support lands.
/// </summary>
public sealed class WindowsServiceHost : IServiceHost
{
    public string DisplayName => "Windows Service Manager";

    public bool IsSupported => OperatingSystem.IsWindows();

    public int Install(ServiceInstallRequest request) => NotImplemented(nameof(Install));
    public int Uninstall(string serviceName) => NotImplemented(nameof(Uninstall));
    public int Start(string serviceName) => NotImplemented(nameof(Start));
    public int Stop(string serviceName) => NotImplemented(nameof(Stop));
    public int Status(string serviceName) => NotImplemented(nameof(Status));

    private static int NotImplemented(string operation)
    {
        Console.Error.WriteLine(
            $"Windows Service '{operation}' is not yet implemented for Squid Tentacle. " +
            "Use 'sc.exe' manually, or track progress at https://github.com/SolarifyDev/Squid/issues.");
        return 2;
    }
}
