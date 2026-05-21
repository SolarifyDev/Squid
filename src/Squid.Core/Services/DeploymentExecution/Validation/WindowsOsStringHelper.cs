using Squid.Message.Constants;

namespace Squid.Core.Services.DeploymentExecution.Validation;

/// <summary>
/// Single source of truth for "does this OS string identify a Windows host?".
///
/// <para>Used by two call sites that need the same tolerance contract:
/// <list type="bullet">
///   <item><c>IISDeployActionHandler.EnsureWindowsTentacleTarget</c> —
///         dispatch-time guard; the runtime safety net.</item>
///   <item><c>MachineCapabilitySet.From</c> — plan-time projection of
///         <c>MachineRuntimeCapabilities.Os</c> into the <c>os: windows</c>
///         capability slot.</item>
/// </list>
/// Before this helper existed, the two call sites carried bit-identical
/// copies of the tolerance logic. A future Windows OS-string variant (e.g.
/// ARM long-form) would have to update both — easy to drift. Centralising
/// here means a single edit covers both surfaces.</para>
///
/// <para><b>Match rules</b>:
/// <list type="bullet">
///   <item>Canonical short form <see cref="AgentOperatingSystems.Windows"/>
///         (<c>"Windows"</c>) emitted by current
///         <c>RuntimeCapabilitiesInspector.DetectOs()</c>.</item>
///   <item>Legacy long forms starting with <c>"Microsoft Windows"</c>
///         (e.g. <c>"Microsoft Windows NT 10.0.19045.0"</c> — what
///         <c>Environment.OSVersion.VersionString</c> returns on modern
///         Windows 10 / 11 / Server). Older Tentacle binaries wrote this
///         directly into the <c>"os"</c> metadata field; cache entries from
///         those agents persist until invalidated.</item>
/// </list></para>
///
/// <para><b>Anti-false-positive anchor</b>: uses
/// <c>StartsWith("Microsoft Windows")</c>, not <c>Contains("Windows")</c>.
/// Strings like <c>"LinuxOnWindowsSubsystem"</c> or
/// <c>"WindowsSomethingElse"</c> are NOT treated as Windows hosts even
/// though they contain the substring <c>"Windows"</c>. Pinned by tests.</para>
/// </summary>
internal static class WindowsOsStringHelper
{
    /// <summary>
    /// Returns true when <paramref name="osValue"/> identifies a Windows host.
    /// Returns false for null / empty / whitespace, for non-Windows OS markers
    /// (<c>"Linux"</c>, <c>"macOS"</c>, etc.), and for strings that merely
    /// contain the substring <c>"Windows"</c> without the canonical prefix.
    /// </summary>
    public static bool IsWindows(string osValue)
    {
        if (string.IsNullOrWhiteSpace(osValue)) return false;

        if (string.Equals(osValue, AgentOperatingSystems.Windows, StringComparison.OrdinalIgnoreCase))
            return true;

        return osValue.StartsWith("Microsoft Windows", StringComparison.OrdinalIgnoreCase);
    }
}
