using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Requests.Machines;

/// <summary>
/// read-only fetch of the agent-reported upgrade-status
/// snapshot for a machine. Returns the typed projection of the agent's
/// <c>last-upgrade.json</c> (Linux <c>/var/lib/squid-tentacle/last-upgrade.json</c>
/// or Windows <c>%PROGRAMDATA%\Squid\Tentacle\upgrade\last-upgrade.json</c>)
/// as last captured by a Capabilities health check.
///
/// <para><b>Why this endpoint exists:</b> the existing
/// <c>GetUpgradeEventTimelineRequest</c> exposes the events JSONL stream
/// (<c>start</c> / <c>method-selected</c> / <c>scope-exec</c> / etc.) but
/// drops the structured <c>exitCode</c> field that ONLY appears in the
/// status payload. Operators investigating a Phase B failure on Windows
/// (e.g. exit 7 = SHA256 mismatch, exit 13 = lock contention, exit 14 =
/// no install method matched) used to need <c>GET /upgrade-log</c> + grep
/// to find the exit code; this endpoint surfaces it as a structured
/// integer field on the operator-facing API.</para>
///
/// <para><b>Use case:</b> operator clicks an "Upgrade failed" task →
/// FE polls this endpoint → gets <c>{ Status: "FAILED", ExitCode: 7,
/// Detail: "SHA256 mismatch", InstallMethod: "zip", ... }</c> → renders
/// a structured failure card with exit-code-keyed remediation links
/// (e.g. exit 7 → "verify download URL"; exit 14 → "check method config").</para>
///
/// <para><b>Empty payload (Data.Status null) when:</b>
/// <list type="bullet">
///   <item>No upgrade has ever run on this machine.</item>
///   <item>Agent's <c>last-upgrade.json</c> file was missing on the last
///         Capabilities probe (operator deleted it manually OR a fresh
///         install hasn't been upgraded yet).</item>
///   <item>Server pod restarted recently — cache cold; refills on next
///         health check. The agent's file is still authoritative.</item>
/// </list>
/// </para>
///
/// <para><b>No side effects, no RPC, no DB hit:</b> reads from the
/// process-local <see cref="GetUpgradeEventTimelineRequest"/>'s sibling
/// in-memory cache (populated on every Capabilities probe). FE can poll
/// at the same 2-3s cadence as the events timeline endpoint.</para>
/// </summary>
[RequiresPermission(Permission.MachineView)]
public class GetUpgradeStatusRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public int MachineId { get; set; }
}

public class GetUpgradeStatusResponse : SquidResponse<GetUpgradeStatusResponseData>
{
}

public class GetUpgradeStatusResponseData
{
    public int MachineId { get; set; }

    /// <summary>
    /// The last-known agent status, or null if no payload has ever been
    /// captured for this machine. Distinguishes "never reported" (null
    /// — Data.Status field absent) from "reported with all-empty fields"
    /// (non-null with empty strings) — the FE can render different UX
    /// for each.
    /// </summary>
    public UpgradeStatusDto Status { get; set; }
}

/// <summary>
/// Wire-friendly projection of the agent's status JSON. Mirrors
/// <c>UpgradeStatusPayload</c> in Squid.Core but lives in Squid.Message
/// so the contract surface stays clean of internal types — same
/// discipline as <see cref="UpgradeEventDto"/> for the events stream.
/// </summary>
public class UpgradeStatusDto
{
    /// <summary>
    /// Schema version reported by the agent. v1 (1.4.x agents) lacks
    /// <see cref="StartedAt"/> / <see cref="ScriptPid"/>; v2 (1.5.0+)
    /// includes both. v2+ Windows/Linux schemas also write
    /// <see cref="ExitCode"/> on terminal status writes.
    /// </summary>
    public int SchemaVersion { get; set; }

    /// <summary>
    /// Reported status (e.g., <c>"IN_PROGRESS"</c>, <c>"SUCCESS"</c>,
    /// <c>"FAILED"</c>, <c>"ROLLED_BACK"</c>, <c>"ROLLBACK_NEEDED"</c>,
    /// <c>"ROLLBACK_CRITICAL_FAILED"</c>, <c>"SWAPPED"</c>). Empty string
    /// when the agent emitted a payload but with no status field —
    /// mirrors the parser's degraded-input handling.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Target version this upgrade was attempting to install.</summary>
    public string TargetVersion { get; set; } = string.Empty;

    /// <summary>
    /// Method that performed (or attempted to perform) the install:
    /// <c>"apt"</c> / <c>"yum"</c> / <c>"tarball"</c> on Linux;
    /// <c>"zip"</c> (and future <c>"chocolatey"</c> / <c>"msi"</c>) on Windows.
    /// </summary>
    public string InstallMethod { get; set; } = string.Empty;

    /// <summary>Operator-facing narrative — used verbatim in the FE failure card.</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>
    /// structured Phase B exit code. The whole point
    /// of the GetUpgradeStatus endpoint is exposing this field that
    /// the existing GetUpgradeEventTimeline endpoint silently drops.
    ///
    /// <para>Null on schema v1 (1.4.x agents) and on IN_PROGRESS writes
    /// (the agent only emits <c>exitCode</c> on terminal-status writes).
    /// Distinguishes "didn't run" (null) from "ran successfully" (0)
    /// — FE can render different UX for each.</para>
    ///
    /// <para>Common exit codes: 0 = success; 1 = unsupported architecture
    /// (Windows .ps1) / generic Linux shell error; 2 = download failure
    /// (zip method); 3 = missing binary in extracted archive; 7 = SHA256
    /// mismatch; 13 = lock contention; 14 = no install method matched;
    /// 15 = insufficient privileges (Windows). Operators investigating
    /// a failed upgrade map this to a remediation runbook.</para>
    /// </summary>
    public int? ExitCode { get; set; }

    /// <summary>When the upgrade script first ran. Null on schema v1.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>
    /// When the agent last touched the status file. Linux .sh writes this
    /// on every status transition; Windows .ps1 omits it currently. Null
    /// when the agent didn't write the field.
    /// </summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// PID of the upgrade script at the time of the last status write.
    /// Operator-debug only (servers can't reach into the agent to verify
    /// liveness). Null on schema v1 + on platforms that don't emit it.
    /// </summary>
    public int? ScriptPid { get; set; }
}
