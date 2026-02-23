using Squid.Tentacle.Abstractions;

namespace Squid.Tentacle.Core;

public sealed class TentacleFlavorResolver
{
    private readonly Dictionary<string, ITentacleFlavor> _flavors;

    public TentacleFlavorResolver(IEnumerable<ITentacleFlavor> flavors)
    {
        _flavors = flavors.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
    }

    public ITentacleFlavor Resolve(string flavorId)
    {
        var requested = string.IsNullOrWhiteSpace(flavorId)
            ? "KubernetesAgent"
            : flavorId;

        if (_flavors.TryGetValue(requested, out var flavor))
            return flavor;

        throw new InvalidOperationException(
            $"Unknown Tentacle flavor '{requested}'. Available: {string.Join(", ", _flavors.Keys.OrderBy(k => k))}");
    }
}
