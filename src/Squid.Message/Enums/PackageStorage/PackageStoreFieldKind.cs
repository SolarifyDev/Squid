using System.Text.Json.Serialization;

namespace Squid.Message.Enums.PackageStorage;

/// <summary>How a setting of a package store kind is entered and where it is kept: <see cref="Secret"/> values are encrypted at rest and never returned. Sent on the wire by name, so reordering the members cannot change what a client reads.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PackageStoreFieldKind
{
    Text = 0,
    Secret = 1,
    Select = 2,
    Path = 3,
    Toggle = 4
}
