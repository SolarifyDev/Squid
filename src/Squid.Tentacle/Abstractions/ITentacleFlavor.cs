namespace Squid.Tentacle.Abstractions;

public interface ITentacleFlavor
{
    string Id { get; }

    /// <summary>
    /// Legacy flavor ids that still resolve to this flavor. Lets a flavor be renamed
    /// (e.g. "LinuxTentacle" → "Tentacle") without breaking already-deployed agents,
    /// install snippets, or configs that pass the old <c>--flavor</c> value. Default: none.
    /// </summary>
    IReadOnlyCollection<string> Aliases => System.Array.Empty<string>();

    TentacleFlavorRuntime CreateRuntime(TentacleFlavorContext context);
}
