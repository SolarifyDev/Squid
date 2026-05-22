namespace Squid.Message.Enums;

/// <summary>
/// Structured reason code returned by the upgrade-info endpoint alongside the
/// human-readable <c>Reason</c> string. The frontend uses the code to drive UI
/// behaviour (hide button vs. show actionable hint with deep-link to health
/// check); the human string is the operator-facing tooltip.
///
/// <para><b>Wire stability (Rule 8 — operator-facing surface)</b>: this enum's
/// names AND numeric values are wire contract. Renaming a member breaks every
/// FE / SDK consumer parsing the response; re-ordering breaks anything that
/// serialises by ordinal. Pinned by
/// <c>UpgradeEligibilityReasonIntegrityTests</c>. <b>Add new members at the
/// END only.</b></para>
/// </summary>
public enum UpgradeEligibilityReason
{
    /// <summary>
    /// Machine endpoint JSON is malformed or missing <c>CommunicationStyle</c>.
    /// Operator action: re-register the machine.
    /// </summary>
    NoCommunicationStyle = 0,

    /// <summary>
    /// OS not yet detected for this machine — the capability cache is cold
    /// (server pod restarted recently and cache hasn't been re-populated) OR
    /// the agent's <c>CapabilitiesResponse</c> didn't include the <c>os</c>
    /// metadata field. The Linux upgrade strategy historically claimed the
    /// Unknown case as a default, producing misleading "Docker Hub unreachable"
    /// messages for Windows machines that hadn't had a health check yet. Under
    /// H1 we short-circuit BEFORE strategy resolution and return this code.
    ///
    /// <para>Operator action: trigger an active health check
    /// (<c>POST /api/machines/{id}/health-check</c> or click "Health Check" in
    /// the UI). If the cache stays empty after a health check, the agent is
    /// likely old enough to predate <c>os</c> metadata — upgrade the agent via
    /// the manual install path.</para>
    /// </summary>
    NoOsDetected = 1,

    /// <summary>
    /// The <c>CommunicationStyle</c> has no in-UI upgrade strategy registered.
    /// E.g. <c>Ssh</c> targets — they're managed by the host's package
    /// manager, not by Squid. Operator action: follow the style's manual
    /// upgrade procedure.
    /// </summary>
    StyleNotSupported = 2,

    /// <summary>
    /// Version registry unreachable AND no env-var override pinned. The human
    /// message mentions the correct registry endpoint and override env var for
    /// this OS (GitHub Releases + <c>SQUID_TARGET_WINDOWS_TENTACLE_VERSION</c>
    /// for Windows; Docker Hub + <c>SQUID_TARGET_LINUX_TENTACLE_VERSION</c>
    /// for Linux). Before H1 this message hardcoded the Linux variant
    /// regardless of OS.
    /// </summary>
    RegistryUnreachable = 3,

    /// <summary>
    /// Eligible: current agent version is unknown (machine has not reported a
    /// version yet, but OS IS known so we know which registry to query). The
    /// agent's per-version idempotency lock will no-op the dispatch if the
    /// agent is already on the target.
    /// </summary>
    EligibleCurrentVersionUnknown = 4,

    /// <summary>
    /// Eligible: one side of the version comparison is non-semver (typically a
    /// dev build like <c>"custom-build-20260419"</c>). Strict comparison isn't
    /// possible, so we conservatively allow the upgrade — worst case the
    /// agent-side idempotency lock no-ops the run.
    /// </summary>
    EligibleNonSemverComparison = 5,

    /// <summary>
    /// Eligible (happy path): latest published version is strictly newer than
    /// current. FE renders the upgrade button.
    /// </summary>
    Eligible = 6,

    /// <summary>
    /// Current version equals latest. Nothing to do; FE hides the button.
    /// </summary>
    AlreadyUpToDate = 7,

    /// <summary>
    /// Current version is newer than latest published — would be a downgrade.
    /// FE hides the button. Operator can explicitly opt in via
    /// <c>AllowDowngrade=true</c> on the upgrade dispatch endpoint.
    /// </summary>
    WouldBeDowngrade = 8
}
