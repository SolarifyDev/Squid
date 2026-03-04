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
        if (string.IsNullOrWhiteSpace(flavorId))
            throw new InvalidOperationException(
                $"Tentacle flavor not configured. Set the Tentacle:Flavor setting. Available: {string.Join(", ", _flavors.Keys.OrderBy(k => k))}");

        if (_flavors.TryGetValue(flavorId, out var flavor))
            return flavor;

        throw new InvalidOperationException(
            $"Unknown Tentacle flavor '{flavorId}'. Available: {string.Join(", ", _flavors.Keys.OrderBy(k => k))}");
    }
}
