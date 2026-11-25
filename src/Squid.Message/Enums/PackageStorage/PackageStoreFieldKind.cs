namespace Squid.Message.Enums.PackageStorage;

/// <summary>How a setting of a package store kind is entered and where it is kept: <see cref="Secret"/> values are encrypted at rest and never returned.</summary>
public enum PackageStoreFieldKind
{
    Text = 0,
    Secret = 1,
    Select = 2,
    Path = 3,
    Toggle = 4
}
