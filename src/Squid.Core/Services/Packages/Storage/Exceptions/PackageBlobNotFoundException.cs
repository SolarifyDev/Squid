namespace Squid.Core.Services.Packages.Storage.Exceptions;

public sealed class PackageBlobNotFoundException(string key) : InvalidOperationException($"Package blob \"{key}\" does not exist in the store")
{
    public string Key { get; } = key;
}
