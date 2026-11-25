namespace Squid.Core.Services.Packages.Storage.Exceptions;

/// <summary>The settings of a store do not match what its kind accepts: a required one is missing, or one is not a setting of the kind.</summary>
public sealed class PackageStoreConfigurationInvalidException(string kind, IReadOnlyList<string> problems) : InvalidOperationException($"The settings for package store kind \"{kind}\" are invalid: {string.Join("; ", problems)}")
{
    public string Kind { get; } = kind;

    public IReadOnlyList<string> Problems { get; } = problems;
}
