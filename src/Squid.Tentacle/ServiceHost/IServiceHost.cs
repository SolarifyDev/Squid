namespace Squid.Tentacle.ServiceHost;

/// <summary>
/// OS-agnostic abstraction over the host's service manager.
/// Concrete impls: systemd (Linux), Windows Services (Windows), launchd (macOS).
/// </summary>
///
/// <remarks>
/// Each method returns a POSIX-style exit code: 0 = success, non-zero = failure.
/// Implementations should print human-readable progress to stdout/stderr so the
/// caller (ServiceCommand) doesn't need to format per-OS diagnostics.
/// </remarks>
public interface IServiceHost
{
    /// <summary>Friendly name of the backing service manager (for logs).</summary>
    string DisplayName { get; }

    /// <summary>Whether this host can be used on the current platform at all.</summary>
    bool IsSupported { get; }

    int Install(ServiceInstallRequest request);
    int Uninstall(string serviceName);
    int Start(string serviceName);
    int Stop(string serviceName);
    int Status(string serviceName);
}

/// <summary>
/// Parameters for <see cref="IServiceHost.Install"/>. OS-neutral — each host
/// translates them into its own unit/plist/SCM format.
/// </summary>
public sealed class ServiceInstallRequest
{
    public string ServiceName { get; init; }
    public string Description { get; init; }
    public string ExecStart { get; init; }

    /// <summary>Directory the service should run in. Must contain any files the binary expects (e.g. appsettings.json).</summary>
    public string WorkingDirectory { get; init; }

    /// <summary>Optional CLI args appended after <see cref="ExecStart"/>.</summary>
    public string[] ExecArgs { get; init; } = [];

    /// <summary>If set, the service runs as this OS user instead of the caller.</summary>
    public string RunAsUser { get; init; }
}
