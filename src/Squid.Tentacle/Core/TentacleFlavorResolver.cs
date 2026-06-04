using Squid.Tentacle.Abstractions;

namespace Squid.Tentacle.Core;

public sealed class TentacleFlavorResolver
{
    private readonly Dictionary<string, ITentacleFlavor> _flavors;

    public TentacleFlavorResolver(IEnumerable<ITentacleFlavor> flavors)
    {
        _flavors = new Dictionary<string, ITentacleFlavor>(StringComparer.OrdinalIgnoreCase);

        foreach (var flavor in flavors)
        {
            _flavors[flavor.Id] = flavor;

            // Register legacy aliases (e.g. "LinuxTentacle" → the renamed "Tentacle" flavor)
            // so old --flavor values from deployed agents / install snippets keep resolving.
            foreach (var alias in flavor.Aliases)
                _flavors[alias] = flavor;
        }
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
