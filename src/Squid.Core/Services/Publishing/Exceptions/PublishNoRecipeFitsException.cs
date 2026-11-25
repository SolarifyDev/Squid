namespace Squid.Core.Services.Publishing.Exceptions;

/// <summary>No recipe fits the package, and none was asked for.</summary>
public sealed class PublishNoRecipeFitsException(string packageId, string version, IReadOnlyList<string> validKinds) : InvalidOperationException($"No recipe fits package '{packageId}' version '{version}'. Name one in the request, or in squid.yaml, from: {string.Join(", ", validKinds)}.")
{
    public string PackageId { get; } = packageId;

    public string Version { get; } = version;

    public IReadOnlyList<string> ValidKinds { get; } = validKinds;
}
