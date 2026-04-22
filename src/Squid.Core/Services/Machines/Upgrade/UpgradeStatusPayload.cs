using System.Text.Json;
using System.Text.Json.Serialization;

namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// Typed projection of the agent-side <c>/var/lib/squid-tentacle/last-upgrade.json</c>
/// file. Written by <c>upgrade-linux-tentacle.sh</c>'s <c>write_status</c>
/// helper at every phase transition; surfaced to the server via the
/// <see cref="Squid.Tentacle.Core.CapabilitiesService.UpgradeStatusMetadataKey"/>
/// metadata entry on every Capabilities RPC.
///
/// <para><b>Schema versioning:</b> <see cref="SchemaVersion"/> gates
/// server-side behaviour that depends on fields added after v1. A v1
/// payload (1.4.x agent) lacks <see cref="StartedAt"/> / <see cref="ScriptPid"/>
/// so staleness detection must NOT trigger on it (would cause false positives
/// on valid mid-flight upgrades). A v2+ payload is the contract that
/// supports staleness detection.</para>
/// </summary>
public sealed record UpgradeStatusPayload
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("targetVersion")]
    public string TargetVersion { get; init; } = string.Empty;

    [JsonPropertyName("installMethod")]
    public string InstallMethod { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>
    /// When the upgrade script FIRST ran (immutable across write_status calls
    /// within a single invocation). Null on schema v1. Used for staleness
    /// detection: an IN_PROGRESS status with startedAt &gt; 10 min ago implies
    /// the script process died without completing — server auto-clears.
    /// </summary>
    [JsonPropertyName("startedAt")]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>
    /// PID of the upgrade bash script at the time of the last status write.
    /// Null on schema v1. Not currently used for detection (servers can't
    /// reach into the agent to verify liveness) but recorded for operator
    /// debugging.
    /// </summary>
    [JsonPropertyName("scriptPid")]
    public int? ScriptPid { get; init; }

    [JsonPropertyName("detail")]
    public string Detail { get; init; } = string.Empty;

    /// <summary>
    /// Deserialise the raw JSON emitted by the agent. Returns null on any
    /// parse failure — server treats "unparseable status" as "no status"
    /// and degrades gracefully (no staleness detection, but all other
    /// capability metadata stays usable). Never throws.
    /// </summary>
    public static UpgradeStatusPayload TryParse(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return null;

        try
        {
            return JsonSerializer.Deserialize<UpgradeStatusPayload>(rawJson);
        }
        catch
        {
            return null;
        }
    }
}
