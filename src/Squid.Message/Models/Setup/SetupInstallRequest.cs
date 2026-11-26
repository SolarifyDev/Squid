namespace Squid.Message.Models.Setup;

/// <summary>Everything the first-run wizard (or an unattended start) decides, in one payload.</summary>
public sealed record SetupInstallRequest
{
    public SetupDatabaseChoice Database { get; init; } = new();
    public SetupRedisChoice Redis { get; init; } = new();
    public SetupAdminAccount Admin { get; init; } = new();
    public SetupServerUrls Server { get; init; } = new();

    /// <summary>Where uploaded packages are kept. Absent means the default local disk store the schema is seeded with.</summary>
    public SetupPackageStorageChoice PackageStorage { get; init; }
}

/// <summary>A package store kind and its settings, keyed by the field keys of that kind's descriptor.</summary>
public sealed record SetupPackageStorageChoice
{
    public string Kind { get; init; } = string.Empty;
    public Dictionary<string, string> Properties { get; init; } = new();
    public Dictionary<string, string> Secrets { get; init; } = new();
}

public sealed record SetupDatabaseChoice
{
    public SetupServiceMode Mode { get; init; }
    public string ConnectionString { get; init; } = string.Empty;
    public bool CreateIfMissing { get; init; } = true;
}

public sealed record SetupRedisChoice
{
    public SetupServiceMode Mode { get; init; }
    public string ConnectionString { get; init; } = string.Empty;
}

public sealed record SetupAdminAccount
{
    public string UserName { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}

public sealed record SetupServerUrls
{
    public string ExternalUrl { get; init; } = string.Empty;
    public string CommsUrl { get; init; } = string.Empty;
}
