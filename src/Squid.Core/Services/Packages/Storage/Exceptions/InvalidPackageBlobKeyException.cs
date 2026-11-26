namespace Squid.Core.Services.Packages.Storage.Exceptions;

public sealed class InvalidPackageBlobKeyException(string key, string reason) : InvalidOperationException($"Package blob key \"{key}\" is not allowed: {reason}")
{
    public string Key { get; } = key;

    public string Reason { get; } = reason;
}
