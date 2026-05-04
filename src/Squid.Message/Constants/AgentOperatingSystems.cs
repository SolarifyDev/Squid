namespace Squid.Message.Constants;

/// <summary>
/// P1-Phase12.E.5 — canonical OS-name strings the Tentacle agent reports
/// in its <c>CapabilitiesResponse.Metadata["os"]</c> dictionary entry, and
/// the server consumes via <c>MachineRuntimeCapabilities.Os</c> for OS-aware
/// upgrade-strategy + version-registry routing.
///
/// <para><b>Why a centralized constant + which assembly:</b> these strings
/// are a CROSS-PROCESS WIRE CONTRACT between agent and server. Drift
/// (e.g. agent rename <c>"Windows"</c> → <c>"WIN"</c> without updating the
/// server's resolver) would silently break Windows tentacle upgrade routing
/// — the resolver would fall through to no-strategy-registered. Putting the
/// strings in <c>Squid.Message</c> (the only assembly both
/// <c>Squid.Core</c> and <c>Squid.Tentacle</c> reference) lets the same
/// const be used on both sides; pinning tests in both assemblies catch a
/// rename at build time.</para>
///
/// <para><b>Pinned per Rule 8</b> by:</para>
/// <list type="bullet">
///   <item><c>RuntimeCapabilitiesInspectorOsConstantsTests</c> in
///         <c>Squid.Tentacle.Tests</c> — the agent (writer) side.</item>
///   <item><c>MachineRuntimeCapabilitiesOsConstantsTests</c> in
///         <c>Squid.UnitTests</c> — the server (reader) side.</item>
/// </list>
/// </summary>
public static class AgentOperatingSystems
{
    /// <summary>Reported when the agent's host runs Windows (Server 2016+ / Win10+).</summary>
    public const string Windows = "Windows";

    /// <summary>Reported when the agent's host runs Linux (any distro).</summary>
    public const string Linux = "Linux";

    /// <summary>Reported when the agent's host runs macOS. No upgrade strategy claims this today (Phase 12.E.5 routing fix); a future <c>MacOSTentacleUpgradeStrategy</c> would plug in without modifying the Linux strategy.</summary>
    public const string MacOS = "macOS";

    /// <summary>Reported when the agent host's OS can't be determined. Treated identically to <c>capabilities.IsUnknown</c> — Linux strategy claims as historical default to preserve pre-Phase-12 behaviour.</summary>
    public const string Unknown = "Unknown";
}
