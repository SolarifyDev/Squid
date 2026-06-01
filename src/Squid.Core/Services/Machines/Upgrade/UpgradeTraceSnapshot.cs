using System.Text.Json.Serialization;

namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// A point-in-time durable snapshot of a machine's upgrade trace: the
/// structured <see cref="UpgradeStatusPayload"/>, the parsed event
/// <see cref="Events"/> timeline, and the Phase B <see cref="Log"/> text — the
/// exact three pieces the in-memory <see cref="IUpgradeEventTimelineStore"/>
/// holds per machine.
///
/// <para>Persisted as a single JSON blob in <c>machine.last_upgrade_trace_json</c>
/// once per upgrade (when the agent first reports a terminal status) and
/// hydrated back into the in-memory store at server startup, so a server pod
/// restart no longer erases how the most recent upgrade concluded.</para>
///
/// <para>The wrapper keys (<c>status</c>/<c>events</c>/<c>log</c>) are explicit
/// so the on-disk shape doesn't depend on a serializer naming policy; the nested
/// <see cref="UpgradeStatusPayload"/> / <see cref="UpgradeEvent"/> already pin
/// their own field names via <see cref="JsonPropertyNameAttribute"/>. The shape
/// is pinned by <c>UpgradeTracePersistenceShapeTests</c>.</para>
/// </summary>
public sealed record UpgradeTraceSnapshot
{
    [JsonPropertyName("status")]
    public UpgradeStatusPayload Status { get; init; }

    [JsonPropertyName("events")]
    public IReadOnlyList<UpgradeEvent> Events { get; init; } = Array.Empty<UpgradeEvent>();

    [JsonPropertyName("log")]
    public string Log { get; init; } = string.Empty;

    /// <summary>
    /// Stable dedup key for the durable persister. Two probes that observe the
    /// SAME terminal outcome produce the same signature, so the persister writes
    /// the snapshot exactly once per concluded upgrade instead of on every probe.
    ///
    /// <para>Built from the status string + the agent's <c>updatedAt</c>
    /// (immutable once the status is terminal — the agent writes it once at
    /// conclusion). On a schema-v1 agent that omits <c>updatedAt</c>, the status
    /// string alone dedups correctly because a terminal status doesn't change
    /// after the upgrade concludes. Not serialised.</para>
    /// </summary>
    [JsonIgnore]
    public string Signature => $"{Status?.Status}@{Status?.UpdatedAt?.ToString("O") ?? "-"}";
}
