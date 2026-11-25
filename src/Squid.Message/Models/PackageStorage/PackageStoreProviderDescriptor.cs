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

    public string Default { get; set; }

    public string Placeholder { get; set; }

    public string HelpText { get; set; }

    /// <summary>The allowed values of a <see cref="PackageStoreFieldKind.Select"/> field.</summary>
    public List<string> Options { get; set; } = new();
}
