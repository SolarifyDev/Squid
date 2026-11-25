namespace Squid.Core.Services.Packages.Storage.Exceptions;

public sealed class PackageStoreNameConflictException(string name) : InvalidOperationException($"A package store named \"{name}\" already exists")
{
    public string Name { get; } = name;
}
