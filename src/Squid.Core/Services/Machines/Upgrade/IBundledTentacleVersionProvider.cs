namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// Returns the Tentacle version this server release recommends.
///
/// <para>
/// Server and Tentacle release together (single git tag triggers both
/// pipelines), so the server's own assembly metadata is the authoritative
/// "what should every agent be on right now" answer. The implementation
/// reads <c>AssemblyInformationalVersion</c> baked in at build time by
/// <c>dotnet publish -p:Version=$IMAGE_TAG</c> in <c>Dockerfile.Api</c>,
/// with an env-var override (<c>SQUID_BUNDLED_TENTACLE_VERSION</c>) for
/// air-gapped / forked deployments and local dev.
/// </para>
///
/// <para>
/// No bundled <c>.txt</c> file — the previous design relied on a manually-
/// edited <c>bundled-tentacle-version.txt</c> resource which silently
/// drifted whenever someone bumped the server version without updating
/// that file. Auto-detection makes drift impossible.
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
