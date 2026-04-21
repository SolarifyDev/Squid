using System.Reflection;

namespace Squid.Tentacle.Core;

/// <summary>
/// Canonical version string for all tentacle-side reporting — Capabilities
/// RPC, upgrade status files, logs, CLI <c>version</c> subcommand.
/// </summary>
/// <remarks>
/// <para><see cref="Assembly.GetName"/>.<c>Version</c> is .NET's 4-component
/// <c>Major.Minor.Build.Revision</c>. We build with <c>-p:Version=X.Y.Z</c>
/// which populates the first three and leaves Revision=0, so
/// <c>Version.ToString()</c> yields <c>"1.3.6.0"</c> — not valid semver and
/// the server's <c>SemVer.TryParse</c> rejects it, producing the
/// "Cannot compare versions strictly" branch in <c>/upgrade-info</c>.</para>
///
/// <para>This helper strips the trailing <c>.0</c> when Revision is zero so
/// every tentacle-side string matches the server's <c>TARGET_VERSION</c>
/// convention (<c>X.Y.Z</c>). Consumers:</para>
/// <list type="bullet">
///   <item><see cref="CapabilitiesService"/> — reported via RPC, drives
///         <c>/upgrade-info.currentVersion</c> and the FE badge.</item>
///   <item><see cref="Commands.VersionCommand"/> — printed to stdout by
///         <c>squid-tentacle version</c>.</item>
///   <item><see cref="ScriptExecution.LocalScriptService"/> — stamped into
///         <c>ExecutionManifest.AgentVersion</c> for deployment audit logs.</item>
/// </list>
/// </remarks>
public static class AssemblyVersion
{
    /// <summary>
    /// Canonical semver string for this binary. Format: <c>Major.Minor.Patch</c>
    /// when Revision is 0 (the normal build case), or the full
    /// <c>Major.Minor.Build.Revision</c> when Revision is non-zero (dev builds
    /// via <c>-p:Version=1.3.6.1</c>).
    /// </summary>
    public static string Canonical { get; } = Compute();

    private static string Compute()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;

        if (v == null) return "0.0.0";

        return v.Revision == 0
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : v.ToString();
    }
}
