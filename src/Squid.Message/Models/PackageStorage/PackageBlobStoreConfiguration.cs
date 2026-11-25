namespace Squid.Message.Models.PackageStorage;

/// <summary>The saved settings of one package store, secrets already decrypted, handed to a provider to build or test a store.</summary>
public class PackageBlobStoreConfiguration : IBaseModel
{
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> Secrets { get; init; } = new Dictionary<string, string>();

    public string Property(string key) => Properties.TryGetValue(key, out var value) ? value : null;

    public string Secret(string key) => Secrets.TryGetValue(key, out var value) ? value : null;
}

public class PackageBlobStoreTestResult : IBaseModel
{
    public bool Success { get; set; }

    /// <summary>Readable by the operator: what was checked, or what went wrong.</summary>
    public string Message { get; set; }
}
