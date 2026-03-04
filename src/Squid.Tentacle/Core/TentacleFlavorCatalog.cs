using System.Reflection;
using Squid.Tentacle.Abstractions;

namespace Squid.Tentacle.Core;

public static class TentacleFlavorCatalog
{
    public static IReadOnlyList<ITentacleFlavor> DiscoverBuiltInFlavors()
    {
        var assembly = Assembly.GetExecutingAssembly();

        return assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(ITentacleFlavor).IsAssignableFrom(t))
            .Where(t => t.GetConstructor(Type.EmptyTypes) != null)
            .Select(t => (ITentacleFlavor)Activator.CreateInstance(t)!)
            .OrderBy(f => f.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
