using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Instance;
using Serilog;

namespace Squid.Tentacle.Commands;

/// <summary>
/// <c>squid-tentacle delete-instance --instance NAME</c>
/// Removes the instance from <c>instances.json</c> and deletes its config +
/// certs directory. Mirrors Octopus's <c>Tentacle.exe delete-instance</c>.
/// </summary>
public sealed class DeleteInstanceCommand : ITentacleCommand
{
    public string Name => "delete-instance";
    public string Description => "Unregister a Tentacle instance and delete its config + certs";

    public Task<int> ExecuteAsync(string[] args, IConfiguration config, CancellationToken ct)
    {
        var (instanceName, _) = InstanceSelector.ExtractInstanceArg(args);

        if (string.IsNullOrWhiteSpace(instanceName))
        {
            Console.Error.WriteLine("Error: --instance NAME is required");
            return Task.FromResult(1);
        }

        var registry = InstanceRegistry.CreateForCurrentProcess();
        var record = registry.Find(instanceName);

        if (record == null)
        {
            Console.Error.WriteLine($"Instance '{instanceName}' not found in {registry.Path}");
            return Task.FromResult(1);
        }

        DeleteIfExists(record.ConfigPath);

        // Wipe the whole per-instance directory (certs + any workspace artefacts stored alongside).
        // InstanceSelector.ResolveCertsPath returns .../instances/{name}/certs; we climb one level
        // up so we clean the enclosing {name}/ folder too, not just certs/.
        var certsDir = InstanceSelector.ResolveCertsPath(record);
        var instanceDir = Path.GetDirectoryName(certsDir);
        DeleteDirectoryIfExists(instanceDir);

        registry.Remove(instanceName);

        Log.Information("Instance '{Name}' deleted", instanceName);
        Console.WriteLine($"Instance '{instanceName}' deleted");

        return Task.FromResult(0);
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: couldn't delete {path}: {ex.Message}");
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: couldn't delete {path}: {ex.Message}");
        }
    }
}
