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
            "uninstall" => Task.FromResult(host.Uninstall(serviceName)),
            "start" => Task.FromResult(host.Start(serviceName)),
            "stop" => Task.FromResult(host.Stop(serviceName)),
            "status" => Task.FromResult(host.Status(serviceName)),
            _ => PrintUsageAndFail()
        };
    }

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
            ExecArgs = execArgs.ToArray()
        });
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
        Console.WriteLine("  install     Create and start a system service");
        Console.WriteLine("  uninstall   Stop and remove the system service");
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
