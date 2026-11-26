using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.PackageStorage;
using Squid.Message.Response;

namespace Squid.Message.Commands.PackageStorage;

[RequiresPermission(Permission.AdministerSystem)]
public class CreatePackageStoreCommand : ICommand
{
    public string Name { get; set; }

    public string Kind { get; set; }

    /// <summary>The non-secret settings, keyed by the field keys of the kind's descriptor.</summary>
    public Dictionary<string, string> Properties { get; set; } = new();

    /// <summary>The secret settings, keyed by the field keys of the kind's descriptor.</summary>
    public Dictionary<string, string> Secrets { get; set; } = new();
}

public class CreatePackageStoreResponse : SquidResponse<PackageStoreDto>
{
}

/// <summary>The kind of a store never changes: its saved settings only mean something to that kind.</summary>
[RequiresPermission(Permission.AdministerSystem)]
public class UpdatePackageStoreCommand : ICommand
{
    public int Id { get; set; }

    public string Name { get; set; }

    public Dictionary<string, string> Properties { get; set; } = new();

    /// <summary>A secret left out (or null) keeps the value already stored; a provided value replaces it.</summary>
    public Dictionary<string, string> Secrets { get; set; } = new();
}

public class UpdatePackageStoreResponse : SquidResponse<PackageStoreDto>
{
}

/// <summary>The default store cannot be deleted: make another store the default first.</summary>
[RequiresPermission(Permission.AdministerSystem)]
public class DeletePackageStoreCommand : ICommand
{
    public int Id { get; set; }
}

public class DeletePackageStoreResponse : SquidResponse
{
}

[RequiresPermission(Permission.AdministerSystem)]
public class SetDefaultPackageStoreCommand : ICommand
{
    public int Id { get; set; }
}

public class SetDefaultPackageStoreResponse : SquidResponse<PackageStoreDto>
{
}

/// <summary>Writes, reads back and removes a probe file in the store, and records the outcome on it.</summary>
[RequiresPermission(Permission.AdministerSystem)]
public class TestPackageStoreCommand : ICommand
{
    public int Id { get; set; }
}

public class TestPackageStoreResponse : SquidResponse<PackageBlobStoreTestResult>
{
}

/// <summary>
/// Tests settings that are not saved (yet): nothing is written, no store is created or updated. <see cref="StoreId"/> optionally names a saved store of
/// the same kind whose stored secrets fill in any secret left out or empty, so an edit can be tested without retyping them.
/// </summary>
[RequiresPermission(Permission.AdministerSystem)]
public class TestPackageStoreSettingsCommand : ICommand
{
    public string Kind { get; set; }

    public Dictionary<string, string> Properties { get; set; } = new();

    public Dictionary<string, string> Secrets { get; set; } = new();

    public int? StoreId { get; set; }
}

public class TestPackageStoreSettingsResponse : SquidResponse<PackageBlobStoreTestResult>
{
}
