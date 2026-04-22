using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// One entry from the agent-side <c>upgrade-events.jsonl</c> file.
/// Each represents a key transition during an upgrade attempt:
/// start → method-selected → scope-exec → restart-start → healthz-pass → success.
/// Server streams these to the UI task activity log in order (B3).
/// </summary>
public sealed record UpgradeEvent
{
    /// <summary>Timestamp of the event (ISO8601 UTC).</summary>
    [JsonPropertyName("t")]
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>Phase indicator: "A" (pre-scope) or "B" (in scope).</summary>
    [JsonPropertyName("phase")]
    public string Phase { get; init; } = string.Empty;

    /// <summary>Short tag identifying the transition: "start", "restart-start", etc.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    /// <summary>Human-readable message for UI display.</summary>
    [JsonPropertyName("msg")]
    public string Message { get; init; } = string.Empty;
}

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

    /// <summary>
    /// Deserialise the agent-side JSONL events file (one event per line,
    /// line order = chronological). Skips malformed / empty lines rather
    /// than failing — a single corrupted event shouldn't hide the dozen
    /// others that are fine. Never throws. Returns empty list if input is
    /// null/empty or every line fails to parse.
    /// </summary>
    public static IReadOnlyList<UpgradeEvent> TryParseEvents(string rawJsonl)
    {
        if (string.IsNullOrWhiteSpace(rawJsonl)) return Array.Empty<UpgradeEvent>();

        var result = new List<UpgradeEvent>();

        using var reader = new StringReader(rawJsonl);
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            UpgradeEvent parsed = null;
            try { parsed = JsonSerializer.Deserialize<UpgradeEvent>(line); } catch { /* skip malformed */ }

            if (parsed != null) result.Add(parsed);
        }

        return result;
    }
}
