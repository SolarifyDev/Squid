using Squid.Message.Enums.PackageStorage;

namespace Squid.Message.Models.PackageStorage;

/// <summary>What a package store kind is called and which settings it takes, so a screen can render its form without knowing the kind.</summary>
public class PackageStoreProviderDescriptor : IBaseModel
{
    public string Kind { get; set; }

    public string DisplayName { get; set; }

    public string Description { get; set; }

    public List<PackageStoreFieldDescriptor> Fields { get; set; } = new();
}

public class PackageStoreFieldDescriptor : IBaseModel
{
    /// <summary>The key the value is saved under. Saved stores refer to it: renaming a key orphans every saved value.</summary>
    public string Key { get; set; }

    public string Label { get; set; }

    public PackageStoreFieldKind Kind { get; set; }

    public bool Required { get; set; }

    /// <summary>Marks the field whose value says where the store keeps its data (a folder, a bucket); a screen shows it as the store's location.</summary>
    public bool IsLocation { get; set; }

    public string Default { get; set; }

    public string Placeholder { get; set; }

    public string HelpText { get; set; }

    /// <summary>The allowed values of a <see cref="PackageStoreFieldKind.Select"/> field.</summary>
    public List<string> Options { get; set; } = new();

    /// <summary>
    /// For a <see cref="PackageStoreFieldKind.Secret"/> field: the keys of the non-secret fields it is bound to (an endpoint, a bucket). A stored secret is
    /// only reused while every bound field still has its stored value; change one and the secret must be entered again, so a stored credential can never
    /// be sent to a destination someone else just typed in. Empty means the secret is not tied to any field.
    /// </summary>
    public List<string> BoundTo { get; set; } = new();
}
