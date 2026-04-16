using System.Diagnostics;

namespace Squid.Tentacle.ServiceHost;

/// <summary>
/// Linux implementation of <see cref="IServiceHost"/>. Generates a systemd unit,
/// reloads the daemon, and drives enable/start/stop/status via <c>systemctl</c>.
/// </summary>
public sealed class SystemdServiceHost : IServiceHost
{
    public string DisplayName => "systemd";

    public bool IsSupported => OperatingSystem.IsLinux();

    public int Install(ServiceInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ServiceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExecStart);

        // Validate the binary exists before writing a unit that systemd will
        // repeatedly fail to start (RestartSec=10 → 6 retries per minute of
        // cryptic "Main process exited, code=exited, status=203/EXEC" in journalctl).
        if (!File.Exists(request.ExecStart))
        {
            Console.Error.WriteLine($"Error: binary not found at {request.ExecStart}");
            Console.Error.WriteLine("  Was the Tentacle installed? Check /opt/squid-tentacle/ or run install-tentacle.sh");
            return 1;
        }

        var unit = BuildUnitFile(request);
        var unitPath = $"/etc/systemd/system/{request.ServiceName}.service";

        try
        {
            File.WriteAllText(unitPath, unit);
            Console.WriteLine($"Created {unitPath}");
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Permission denied writing {unitPath}. Try: sudo squid-tentacle service install");
            return 1;
        }

        var reload = Systemctl("daemon-reload");
        var enable = Systemctl("enable", request.ServiceName);
        var start = Systemctl("start", request.ServiceName);

        if (reload != 0 || enable != 0 || start != 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Service '{request.ServiceName}' unit file written, but systemctl steps failed.");
            Console.Error.WriteLine("  Is systemd available on this host? (Docker/WSL1 often lack it.)");
            return 1;
        }

        Console.WriteLine($"Service '{request.ServiceName}' installed and started.");
        Console.WriteLine($"  Status:  sudo systemctl status {request.ServiceName}");
        Console.WriteLine($"  Logs:    sudo journalctl -u {request.ServiceName} -f");

        return 0;
    }

    public int Uninstall(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        Systemctl("stop", serviceName);
        Systemctl("disable", serviceName);

        var unitPath = $"/etc/systemd/system/{serviceName}.service";

        if (File.Exists(unitPath))
        {
            try
            {
                File.Delete(unitPath);
                Console.WriteLine($"Removed {unitPath}");
            }
            catch (UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Permission denied removing {unitPath}. Try: sudo squid-tentacle service uninstall");
                return 1;
            }
        }

        Systemctl("daemon-reload");
        Console.WriteLine($"Service '{serviceName}' uninstalled.");
        return 0;
    }

    public int Start(string serviceName) => Systemctl("start", serviceName);
    public int Stop(string serviceName) => Systemctl("stop", serviceName);
    public int Status(string serviceName) => Systemctl("status", serviceName);

    internal static string BuildUnitFile(ServiceInstallRequest request)
    {
        var description = string.IsNullOrWhiteSpace(request.Description)
            ? $"Squid Tentacle Agent ({request.ServiceName})"
            : request.Description;

        var execLine = request.ExecArgs is { Length: > 0 }
            ? $"{request.ExecStart} {string.Join(' ', request.ExecArgs)}"
            : request.ExecStart;

        var userLines = !string.IsNullOrWhiteSpace(request.RunAsUser)
            ? $"User={request.RunAsUser}\nGroup={request.RunAsUser}\n"
            : string.Empty;

        return $"""
            [Unit]
            Description={description}
            After=network.target

            [Service]
            Type=simple
            ExecStart={execLine}
            WorkingDirectory={request.WorkingDirectory}
            {userLines}Restart=always
            RestartSec=10
            KillSignal=SIGINT
            TimeoutStopSec=60

            [Install]
            WantedBy=multi-user.target
            """;
    }

    private static int Systemctl(string action, string serviceName = null)
    {
        var args = serviceName != null ? $"{action} {serviceName}" : action;

        try
        {
            var psi = new ProcessStartInfo("systemctl", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var process = Process.Start(psi);
            if (process == null) return 1;

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(TimeSpan.FromSeconds(30));

            if (!string.IsNullOrWhiteSpace(stdout)) Console.Write(stdout);
            if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.Write(stderr);

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to run systemctl {args}: {ex.Message}");
            return 1;
        }
    }
}
