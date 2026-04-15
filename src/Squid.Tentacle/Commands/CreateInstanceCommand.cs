using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Instance;
using Squid.Tentacle.Platform;
using Serilog;

namespace Squid.Tentacle.Commands;

/// <summary>
/// <c>squid-tentacle create-instance [--instance NAME] [--config PATH]</c>
///
/// Registers a new Tentacle instance in <c>instances.json</c> and creates its
/// certs directory. Mirrors Octopus's <c>Tentacle.exe create-instance</c>.
/// Omitting <c>--instance</c> creates the Default instance.
/// </summary>
public sealed class CreateInstanceCommand : ITentacleCommand
{
    public string Name => "create-instance";
    public string Description => "Register a new Tentacle instance";

    public Task<int> ExecuteAsync(string[] args, IConfiguration config, CancellationToken ct)
    {
        var (instanceName, remaining) = InstanceSelector.ExtractInstanceArg(args);
        var explicitConfigPath = ParseOption(remaining, "--config");

        instanceName = string.IsNullOrWhiteSpace(instanceName) ? InstanceRecord.DefaultName : instanceName;

        var registry = InstanceRegistry.CreateForCurrentProcess();

        if (registry.Find(instanceName) != null)
        {
            Console.Error.WriteLine($"Instance '{instanceName}' already exists at {registry.Path}");
            return Task.FromResult(1);
        }

        var configDir = System.IO.Path.GetDirectoryName(registry.Path)!;
        var configPath = !string.IsNullOrWhiteSpace(explicitConfigPath)
            ? explicitConfigPath
            : PlatformPaths.GetInstanceConfigPath(configDir, instanceName);

        var record = new InstanceRecord
        {
            Name = instanceName,
            ConfigPath = configPath,
            CreatedAt = DateTimeOffset.UtcNow
        };

        registry.Add(record);

        // Eagerly create per-instance certs directory so register/new-certificate can land in it.
        var certsDir = PlatformPaths.GetInstanceCertsDir(configDir, instanceName);
        Directory.CreateDirectory(certsDir);

        Log.Information("Instance '{Name}' created", instanceName);
        Console.WriteLine($"Instance '{instanceName}' created");
        Console.WriteLine($"  Registry:  {registry.Path}");
        Console.WriteLine($"  Config:    {configPath}");
        Console.WriteLine($"  Certs dir: {certsDir}");

        return Task.FromResult(0);
    }

    private static string ParseOption(string[] args, string optionName)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals(optionName, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1];

            if (args[i].StartsWith($"{optionName}=", StringComparison.OrdinalIgnoreCase))
                return args[i][(optionName.Length + 1)..];
        }

        return null;
    }
}
