using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace Squid.Tentacle.Commands;

/// <summary>
/// Manages the Tentacle as a systemd service (Linux).
/// Subcommands: install, uninstall, start, stop, status.
/// </summary>
public sealed class ServiceCommand : ITentacleCommand
{
    public string Name => "service";
    public string Description => "Manage the Tentacle systemd service (install, uninstall, start, stop, status)";

    private const string DefaultServiceName = "squid-tentacle";

    public Task<int> ExecuteAsync(string[] args, IConfiguration config, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return Task.FromResult(1);
        }

        var subcommand = args[0].ToLowerInvariant();
        var serviceName = ParseServiceName(args) ?? DefaultServiceName;

        return subcommand switch
        {
            "install" => Task.FromResult(Install(serviceName)),
            "uninstall" => Task.FromResult(Uninstall(serviceName)),
            "start" => Task.FromResult(RunSystemctl("start", serviceName)),
            "stop" => Task.FromResult(RunSystemctl("stop", serviceName)),
            "status" => Task.FromResult(RunSystemctl("status", serviceName)),
            _ => PrintUsageAndFail()
        };
    }

    private static int Install(string serviceName)
    {
        var executablePath = GetExecutablePath();
        var workingDir = Path.GetDirectoryName(executablePath) ?? "/opt/squid-tentacle";

        var unit = $"""
            [Unit]
            Description=Squid Tentacle Agent ({serviceName})
            After=network.target

            [Service]
            Type=simple
            ExecStart={executablePath} run
            WorkingDirectory={workingDir}
            Restart=always
            RestartSec=10
            KillSignal=SIGINT
            TimeoutStopSec=60

            [Install]
            WantedBy=multi-user.target
            """;

        var unitPath = $"/etc/systemd/system/{serviceName}.service";

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

        RunSystemctl("daemon-reload", null);
        RunSystemctl("enable", serviceName);
        RunSystemctl("start", serviceName);

        Console.WriteLine($"Service '{serviceName}' installed and started.");
        Console.WriteLine($"  Status:  sudo systemctl status {serviceName}");
        Console.WriteLine($"  Logs:    sudo journalctl -u {serviceName} -f");

        return 0;
    }

    private static int Uninstall(string serviceName)
    {
        RunSystemctl("stop", serviceName);
        RunSystemctl("disable", serviceName);

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

        RunSystemctl("daemon-reload", null);
        Console.WriteLine($"Service '{serviceName}' uninstalled.");

        return 0;
    }

    internal static string GenerateUnitFile(string serviceName, string executablePath)
    {
        var workingDir = Path.GetDirectoryName(executablePath) ?? "/opt/squid-tentacle";

        return $"""
            [Unit]
            Description=Squid Tentacle Agent ({serviceName})
            After=network.target

            [Service]
            Type=simple
            ExecStart={executablePath} run
            WorkingDirectory={workingDir}
            Restart=always
            RestartSec=10
            KillSignal=SIGINT
            TimeoutStopSec=60

            [Install]
            WantedBy=multi-user.target
            """;
    }

    private static int RunSystemctl(string action, string serviceName)
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

    private static string GetExecutablePath()
    {
        var entryAssembly = System.Reflection.Assembly.GetEntryAssembly();

        if (entryAssembly != null)
        {
            var dllPath = entryAssembly.Location;

            if (!string.IsNullOrEmpty(dllPath))
                return $"dotnet {dllPath}";
        }

        return "dotnet Squid.Tentacle.dll";
    }

    private static string ParseServiceName(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--service-name")
                return args[i + 1];
        }

        return null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: squid-tentacle service <subcommand> [--service-name NAME]");
        Console.WriteLine();
        Console.WriteLine("Subcommands:");
        Console.WriteLine("  install     Create and start a systemd service");
        Console.WriteLine("  uninstall   Stop and remove the systemd service");
        Console.WriteLine("  start       Start the service");
        Console.WriteLine("  stop        Stop the service");
        Console.WriteLine("  status      Show service status");
    }

    private static Task<int> PrintUsageAndFail()
    {
        PrintUsage();
        return Task.FromResult(1);
    }
}
