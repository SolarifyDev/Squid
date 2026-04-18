using Squid.Message.Enums;

namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// The authoritative source for "what is the latest published Tentacle version
/// suitable as the upgrade target" — queries the actual delivery channel
/// where each Tentacle flavor is published, NOT the Squid Server's own
/// version. This decoupling matters because:
///
/// <list type="bullet">
///   <item>Tentacle and Server can release independently (security hotfix
///         on Tentacle without rebuilding Server, or vice versa).</item>
///   <item>The Server's own assembly version is meaningless for "what
///         binary should agents run" — it answers a different question
///         ("what code is the orchestrator on").</item>
///   <item>An air-gapped customer might ship their own Tentacle fork from a
///         private mirror; the env-var override below is the escape hatch.</item>
/// </list>
///
/// <para><b>Resolution order</b> (first non-empty wins):</para>
/// <list type="number">
///   <item>Style-specific env var override
///         (<c>SQUID_TARGET_LINUX_TENTACLE_VERSION</c> / <c>SQUID_TARGET_K8S_AGENT_VERSION</c>) —
///         operator escape hatch for canary, air-gap, or pinning.</item>
///   <item>In-process TTL cache (default 10 min) of last successful Docker
///         Hub query — keeps the path responsive for per-machine upgrade
///         calls without hammering the registry.</item>
///   <item>Live Docker Hub tags query — the real source of truth.</item>
///   <item>Stale cache fallback (last successful value, even if older than
///         TTL) when Docker Hub is down — better than refusing all upgrades.</item>
///   <item>Empty + warning log when nothing else worked; the operator must
///         pass <c>TargetVersion</c> explicitly.</item>
/// </list>
/// </summary>
public interface ITentacleVersionRegistry : ISingletonDependency
{
    /// <summary>
    /// Latest published version for the target style. Different styles ship
    /// via different channels:
    /// <list type="bullet">
    ///   <item><c>TentaclePolling</c> / <c>TentacleListening</c> →
    ///         Docker Hub <c>squidcd/squid-tentacle-linux</c> (mirrors the
    ///         GitHub Releases tarball publication).</item>
    ///   <item><c>KubernetesAgent</c> → Docker Hub <c>squidcd/squid-tentacle</c>
    ///         (the helm chart's image).</item>
    /// </list>
    /// </summary>
    Task<string> GetLatestVersionAsync(string communicationStyle, CancellationToken ct);

    /// <summary>
    /// Canonical GitHub Releases tarball URL for a Linux tentacle version.
    /// Centralised here so air-gapped customers can fork this method to
    /// point at a private mirror, and so the URL pattern lives next to the
    /// version it's paired with — not scattered across upgrade strategies.
    /// </summary>
    string GetLinuxDownloadUrl(string version, string rid);
}
