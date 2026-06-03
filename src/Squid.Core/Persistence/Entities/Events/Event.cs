using Squid.Message.Enums.Events;

namespace Squid.Core.Persistence.Entities.Events;

/// <summary>
/// An append-only audit event — Squid's persisted "history" stream (parity with
/// Octopus's Event document). Records WHAT happened, WHO did it, WHEN, and HOW
/// their identity was established, plus the related documents it should appear
/// against.
///
/// <para>Deliberately NOT <see cref="IAuditable"/>: the event IS the audit
/// record — it is immutable and never updated, so the 4 IAuditable columns would
/// be dead weight on a high-volume table. <see cref="Occurred"/> is its only
/// timestamp.</para>
///
/// <para>It is a THIN POINTER: it stores ids referencing the deployment /
/// task / etc. but never copies their fat detail (script output lives in
/// ServerTaskLog, intervention forms in DeploymentInterruption, etc.). Document
/// snapshots live in the lazy side table <see cref="EventDocumentSnapshot"/>.</para>
/// </summary>
public class Event : IEntity<long>
{
    /// <summary>Monotonic key — also the cursor for keyset pagination.</summary>
    public long Id { get; set; }

    public EventCategory Category { get; set; }

    /// <summary>
    /// Structured message arguments (jsonb), e.g.
    /// <c>{"Project":{"id":643,"name":"Smarties.Api"},"Release":{"name":"6.6.2"},"Environment":{"id":3,"name":"PRD"}}</c>.
    /// The UI renders + linkifies the category's message template against these,
    /// so the English template text is not duplicated into every row.
    /// </summary>
    public string ReferencesJson { get; set; } = "{}";

    public int SpaceId { get; set; }

    // ── First-class related-document columns (indexed) for the known history
    //    access paths (release / project / environment / machine pages). Btree
    //    on these is faster + smaller than a jsonb-GIN relation blob. ──
    public int? ProjectId { get; set; }
    public int? ReleaseId { get; set; }
    public int? DeploymentId { get; set; }
    public int? EnvironmentId { get; set; }
    public int? MachineId { get; set; }
    public int? ServerTaskId { get; set; }

    // ── Provenance ──
    public int? UserId { get; set; }

    /// <summary>Denormalized actor name snapshot — survives user rename/delete so the audit stays accurate.</summary>
    public string Username { get; set; } = "system";

    public EventIdentityEstablishedWith EstablishedWith { get; set; }

    public string? UserAgent { get; set; }

    public DateTimeOffset Occurred { get; set; }
}
