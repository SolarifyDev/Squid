using System.Text.Json.Serialization;

namespace Squid.Message.Models.Setup;

/// <summary>Where a backing service (PostgreSQL / Redis) comes from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SetupServiceMode
{
    /// <summary>Started and owned by the Squid process, data kept in the data directory.</summary>
    Builtin,

    /// <summary>An operator-provided server reached through a connection string.</summary>
    External
}
