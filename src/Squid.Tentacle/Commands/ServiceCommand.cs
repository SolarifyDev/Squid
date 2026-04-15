using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Instance;
using Squid.Tentacle.ServiceHost;

namespace Squid.Tentacle.Commands;

/// <summary>
/// Manages the Tentacle as a system service. Backends are chosen per-OS
/// via <see cref="ServiceHostFactory"/>: systemd on Linux, Windows Services on
/// Windows, launchd on macOS. The command itself only knows about the generic
/// <see cref="IServiceHost"/> contract.
///
/// Subcommands: install, uninstall, start, stop, status.
/// Options: <c>--instance NAME</c> (default <c>Default</c>), <c>--service-name NAME</c>.
/// </summary>
public sealed class ServiceCommand : ITentacleCommand
{
    public string Name => "service";
    public string Description => "Manage the Tentacle system service (install, uninstall, start, stop, status)";

    private const string DefaultServiceName = "squid-tentacle";

    public Task<int> ExecuteAsync(string[] args, IConfiguration config, CancellationToken ct)
    {
        var (instanceName, argsWithoutInstance) = InstanceSelector.ExtractInstanceArg(args);

        if (argsWithoutInstance.Length == 0)
        {
            PrintUsage();
            return Task.FromResult(1);
        }

        var subcommand = argsWithoutInstance[0].ToLowerInvariant();
        var instance = InstanceSelector.Resolve(instanceName);

        // Service name defaults to "squid-tentacle" for the Default instance, or
        // "squid-tentacle-{instance}" for named instances, so multi-instance hosts
        // don't collide on the same systemd unit name.
        var serviceName = ParseOption(argsWithoutInstance, "--service-name")
            ?? (instance.Name.Equals(InstanceRecord.DefaultName, StringComparison.OrdinalIgnoreCase)
                ? DefaultServiceName
                : $"{DefaultServiceName}-{instance.Name}");

        var host = ServiceHostFactory.Resolve();

        return subcommand switch
        {
            "install" => Task.FromResult(Install(host, instance, serviceName)),
            "uninstall" => Task.FromResult(Uninstall(host, instance, serviceName, purge: HasFlag(argsWithoutInstance, "--purge"))),
            "start" => Task.FromResult(host.Start(serviceName)),
            "stop" => Task.FromResult(host.Stop(serviceName)),
            "status" => Task.FromResult(host.Status(serviceName)),
            _ => PrintUsageAndFail()
        };
    }

    /// <summary>
    /// Removes the system service and (with <c>--purge</c>) everything the
    /// instance left on disk: workspace, certs, and its <c>instances.json</c>
    /// entry. Without <c>--purge</c> only the unit file is removed, matching
    /// the historical behaviour for operators who want to reinstall later
    /// while keeping cert identity.
    /// </summary>
    private static int Uninstall(IServiceHost host, InstanceRecord instance, string serviceName, bool purge)
    {
        var serviceExit = host.Uninstall(serviceName);

        if (!purge)
            return serviceExit;

        PurgeInstanceArtefacts(instance);

        return serviceExit;
    }

    private static void PurgeInstanceArtefacts(InstanceRecord instance)
    {
        // Delete the config.json + per-instance dir (certs + workspace staging).
        DeleteFileQuietly(instance.ConfigPath, "config file");

        var certsDir = InstanceSelector.ResolveCertsPath(instance);
        var instanceDir = Path.GetDirectoryName(certsDir);
        DeleteDirectoryQuietly(instanceDir, "instance directory");

        // Remove the registry entry so list-instances stops showing it.
        try
        {
            InstanceRegistry.CreateForCurrentProcess().Remove(instance.Name);
            Console.WriteLine($"Removed '{instance.Name}' from instance registry");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: couldn't update instance registry: {ex.Message}");
        }
    }

    private static void DeleteFileQuietly(string path, string label)
    {
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                Console.WriteLine($"Removed {label}: {path}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: couldn't delete {label} at {path}: {ex.Message}");
        }
    }

    private static void DeleteDirectoryQuietly(string path, string label)
    {
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                Console.WriteLine($"Removed {label}: {path}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: couldn't delete {label} at {path}: {ex.Message}");
        }
    }

    private static bool HasFlag(string[] args, string flag)
    {
        foreach (var a in args)
        {
            if (a.Equals(flag, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Dedicated non-login system user the service runs as when present on the
    /// host. Created by <c>install-tentacle.sh</c> (or manually via
    /// <c>useradd -r squid-tentacle</c>). Running as this user instead of root
    /// means a malicious deployment script can't trivially pivot to host admin.
    /// Falls back to root (unset) if the user doesn't exist.
    /// </summary>
    internal const string DefaultServiceUser = "squid-tentacle";

    private static int Install(IServiceHost host, InstanceRecord instance, string serviceName)
    {
        var (execStart, workingDir) = ResolveServiceExecution();

        // Pass --instance NAME so the service loads the right config file; skip the flag
        // entirely for Default since that's the zero-config default anyway.
        var execArgs = new List<string> { "run" };

        if (!instance.Name.Equals(InstanceRecord.DefaultName, StringComparison.OrdinalIgnoreCase))
        {
            execArgs.Add("--instance");
            execArgs.Add(instance.Name);
        }

        return host.Install(new ServiceInstallRequest
        {
            ServiceName = serviceName,
            Description = $"Squid Tentacle Agent ({instance.Name})",
            ExecStart = execStart,
            WorkingDirectory = workingDir,
            ExecArgs = execArgs.ToArray(),
            RunAsUser = DetectServiceUser()
        });
    }

    /// <summary>
    /// Returns <see cref="DefaultServiceUser"/> if the OS has that user and
    /// we're on Linux; otherwise null (service host interprets null as "run
    /// as the caller / root"). Windows and macOS ignore this for now.
    /// </summary>
    private static string DetectServiceUser()
    {
        if (!OperatingSystem.IsLinux()) return null;

        try
        {
            // `getent passwd USER` exits 0 and prints a line when the user exists, non-zero otherwise.
            var psi = new System.Diagnostics.ProcessStartInfo("getent", $"passwd {DefaultServiceUser}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;

            proc.WaitForExit(TimeSpan.FromSeconds(5));
            return proc.ExitCode == 0 ? DefaultServiceUser : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves <c>ExecStart</c> and <c>WorkingDirectory</c> for the service unit.
    /// Uses <see cref="Environment.ProcessPath"/> (real exe — works for single-file
    /// PublishSingleFile deployments) + <see cref="AppContext.BaseDirectory"/> (install dir).
    /// </summary>
    internal static (string ExecStart, string WorkingDir) ResolveServiceExecution()
    {
        var workingDir = (AppContext.BaseDirectory ?? "/opt/squid-tentacle").TrimEnd('/', '\\');
        var execStart = Environment.ProcessPath;

        if (string.IsNullOrEmpty(execStart))
            execStart = Path.Combine(workingDir, "Squid.Tentacle");

        return (execStart, workingDir);
    }

    /// <summary>
    /// Kept for tests written against the old signature — delegates to systemd unit builder.
    /// New call-sites should go through <see cref="ServiceHostFactory"/>.
    /// </summary>
    internal static string GenerateUnitFile(string serviceName, string execStart, string workingDir)
    {
        return SystemdServiceHost.BuildUnitFile(new ServiceInstallRequest
        {
            ServiceName = serviceName,
            ExecStart = execStart,
            WorkingDirectory = workingDir
        });
    }

    private static string ParseOption(string[] args, string optionName)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(optionName, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: squid-tentacle service <subcommand> [--instance NAME] [--service-name NAME]");
        Console.WriteLine();
        Console.WriteLine("Subcommands:");
        Console.WriteLine("  install              Create and start a system service");
        Console.WriteLine("  uninstall [--purge]  Stop and remove the service; --purge also wipes");
        Console.WriteLine("                       the instance config, certs, and registry entry");
        Console.WriteLine("  start                Start the service");
        Console.WriteLine("  stop                 Stop the service");
        Console.WriteLine("  status               Show service status");
    }

    private static Task<int> PrintUsageAndFail()
    {
        PrintUsage();
        return Task.FromResult(1);
    }
}
