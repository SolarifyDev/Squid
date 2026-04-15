using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Instance;

namespace Squid.Tentacle.Commands;

/// <summary>
/// <c>squid-tentacle list-instances</c> — print all registered instances
/// as a simple table. Mirrors Octopus's <c>Tentacle.exe list-instances</c>.
/// </summary>
public sealed class ListInstancesCommand : ITentacleCommand
{
    public string Name => "list-instances";
    public string Description => "List all registered Tentacle instances";

    public Task<int> ExecuteAsync(string[] args, IConfiguration config, CancellationToken ct)
    {
        var registry = InstanceRegistry.CreateForRead();
        var instances = registry.List();

        if (instances.Count == 0)
        {
            Console.WriteLine("No instances registered.");
            Console.WriteLine($"  Registry looked at: {registry.Path}");
            Console.WriteLine("  Run 'squid-tentacle create-instance --instance NAME' to create one.");
            return Task.FromResult(0);
        }

        Console.WriteLine($"Instances registered at {registry.Path}:");
        Console.WriteLine();

        var nameColWidth = Math.Max(4, instances.Max(i => i.Name.Length));

        Console.WriteLine($"  {"NAME".PadRight(nameColWidth)}  CONFIG");
        Console.WriteLine($"  {new string('-', nameColWidth)}  ------");

        foreach (var instance in instances.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"  {instance.Name.PadRight(nameColWidth)}  {instance.ConfigPath}");

        return Task.FromResult(0);
    }
}
