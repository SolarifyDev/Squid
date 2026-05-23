namespace Squid.Message.Models.Machines;

/// <summary>
/// H3 — structured outcome of a manual / active health-check probe. Returned
/// by <c>IMachineHealthCheckService.ManualHealthCheckAsync</c> and threaded
/// into <c>RunMachineHealthCheckResponse.Data</c> so the frontend / CLI can
/// distinguish "agent unreachable" from "probe completed but reported error"
/// without re-parsing the human-readable Detail string.
///
/// <para><b>Why this exists</b>: in 1.7.x clicking "Health Check" returned
/// an empty success/failure shape. Operators couldn't tell whether the probe
/// actually re-read agent metadata (the entire UX point of the button) or
/// just succeeded as a stale read. The user reported "點了 health check 還是
/// 一樣的" — they had no signal that the active probe had been attempted
/// and what it found.</para>
/// </summary>
public sealed class ManualHealthCheckResult
{
    /// <summary>True iff the Halibut Capabilities RPC round-tripped AND the
    /// agent returned a non-null response.</summary>
    public bool Successful { get; init; }

    /// <summary>Human-readable detail line. Same wording as
    /// <c>Machine.HealthDetail</c> persisted to DB — safe to render in a
    /// tooltip without further processing.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>Structured error category for failed probes. <c>null</c> on
    /// success. Stable wire values: <c>"machine_disabled"</c> /
    /// <c>"no_health_checker"</c> / <c>"agent_unreachable"</c> /
    /// <c>"machine_not_found"</c>. Pinned by integrity test.</summary>
    public string ErrorCode { get; init; }

    /// <summary>Agent's reported version after the probe (from cache snapshot
    /// at probe completion). Empty when the probe failed or the agent didn't
    /// report a version field.</summary>
    public string AgentVersion { get; init; } = string.Empty;

    /// <summary>Agent's reported OS string after the probe — could be the
    /// canonical <c>"Windows"</c> / <c>"Linux"</c> or the legacy long form
    /// <c>"Microsoft Windows NT 10.0.19045.0"</c>. Both are accepted by
    /// <see cref="Squid.Core.Services.DeploymentExecution.Validation.WindowsOsStringHelper"/>.</summary>
    public string Os { get; init; } = string.Empty;

    /// <summary>UTC timestamp of when the probe completed (regardless of
    /// outcome). Matches the value persisted to
    /// <c>machine.runtime_capabilities_updated_at</c> on success — so the FE
    /// can compare against the previously-shown value and confirm the probe
    /// actually refreshed.</summary>
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Stable wire literals for <see cref="ManualHealthCheckResult.ErrorCode"/>.
/// Renaming a constant here breaks any FE / SDK consumer parsing the
/// response — pinned by integrity test (Rule 8).
/// </summary>
public static class ManualHealthCheckErrorCodes
{
    public const string MachineNotFound = "machine_not_found";
    public const string MachineDisabled = "machine_disabled";
    public const string NoHealthChecker = "no_health_checker";
    public const string AgentUnreachable = "agent_unreachable";
}
