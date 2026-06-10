namespace Squid.Message.Enums.Events;

/// <summary>
/// The kind of audited event. Stored as <c>smallint</c> (the enum is
/// <c>short</c>-backed) to keep the high-volume <c>event</c> table lean.
///
/// <para>Values are explicit and MUST NOT be renumbered — they are persisted.
/// Numbering is grouped (1-9 document audit, 10+ deployment lifecycle) with
/// gaps left for future additions within each group.</para>
/// </summary>
public enum EventCategory : short
{
    // ── Document audit (generic — any persisted entity) ──────────────────────
    DocumentCreated = 1,
    DocumentModified = 2,
    DocumentDeleted = 3,

    // ── Deployment lifecycle ─────────────────────────────────────────────────
    DeploymentQueued = 10,
    DeploymentStarted = 11,
    ManualInterventionRaised = 12,
    ManualInterventionSubmitted = 13,
    GuidedFailureRaised = 14,
    DeploymentResumed = 15,
    DeploymentSucceeded = 16,
    DeploymentFailed = 17,
    DeploymentCanceled = 18,
    DeploymentTimedOut = 19
}
