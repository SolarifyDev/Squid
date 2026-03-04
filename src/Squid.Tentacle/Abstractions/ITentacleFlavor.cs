namespace Squid.Tentacle.Abstractions;

public interface ITentacleFlavor
{
    string Id { get; }

    TentacleFlavorRuntime CreateRuntime(TentacleFlavorContext context);
}
