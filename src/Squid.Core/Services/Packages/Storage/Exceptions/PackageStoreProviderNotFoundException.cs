namespace Squid.Core.Services.Packages.Storage.Exceptions;

/// <summary>A saved store (or a request) names a kind that no provider on this server implements.</summary>
public sealed class PackageStoreProviderNotFoundException(string kind) : InvalidOperationException($"No package store provider is registered for kind \"{kind}\"")
{
    public string Kind { get; } = kind;
}
