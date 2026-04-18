namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// Returns the Tentacle version this server release recommends.
///
/// <para>
/// Mirrors Octopus's <c>IBundledPackageStore</c> design without the package-
/// store overhead: the version is a single string baked into the server
/// release at build time so a deployment of Squid Server 1.4.0 always pushes
/// agents to a specific known-good Tentacle version (e.g. 1.4.0). This makes
/// "what should every agent be on right now" a question with a single
/// authoritative answer rather than something operators have to track in a
/// spreadsheet.
/// </para>
///
/// <para>
/// Implementation reads from the embedded resource
/// <c>Squid.Core.Resources.Upgrade.bundled-tentacle-version.txt</c>; the file
/// is overwritten at CI time by the workflow that bumps server version, so
/// the bundled-tentacle version always tracks server version unless an
/// operator explicitly pins via the <c>UpgradeMachineCommand.TargetVersion</c>
/// override.
/// </para>
/// </summary>
public interface IBundledTentacleVersionProvider : IScopedDependency
{
    /// <summary>
    /// The server's recommended Tentacle version. Empty string when the
    /// resource is missing (treat as "no recommendation"; callers should
    /// require the operator to specify a version explicitly).
    /// </summary>
    string GetBundledVersion();

    /// <summary>
    /// Returns the canonical download URL for a given version's Linux tarball.
    /// Used by the upgrade strategy to instruct the agent where to fetch the
    /// new binary from.
    /// </summary>
    /// <param name="version">Semver string (e.g. <c>1.4.0</c>) — must match a published release tag.</param>
    /// <param name="rid">.NET Runtime Identifier (e.g. <c>linux-x64</c>, <c>linux-arm64</c>).</param>
    string GetDownloadUrl(string version, string rid);
}
