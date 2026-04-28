namespace Squid.Tentacle.Platform;

/// <summary>
/// P1-Phase12.A.2 (Windows Tentacle foundations) — abstraction over the
/// agent's on-disk upgrade-status / events / log files.
///
/// <para><b>Why this exists</b>: pre-Phase-12 the agent's
/// <c>CapabilitiesService</c> hardcoded three Linux-specific paths
/// (<c>/var/lib/squid-tentacle/last-upgrade.json</c>, etc.) and read
/// them via three private static <c>Func&lt;string&gt;</c> readers.
/// As Windows Tentacle support comes online, the upgrade flow will
/// write to the equivalent Windows paths
/// (<c>%PROGRAMDATA%\Squid\Tentacle\upgrade\...</c>) and the
/// metadata-emit path needs to read from there instead.</para>
///
/// <para>Three concrete implementations:
/// <list type="bullet">
///   <item><see cref="LinuxUpgradeStatusStorage"/> — preserves the
///         exact pre-Phase-12 paths bit-for-bit; matches what
///         <c>upgrade-linux-tentacle.sh</c> writes.</item>
///   <item><see cref="WindowsUpgradeStatusStorage"/> — reads from
///         <c>%PROGRAMDATA%\Squid\Tentacle\upgrade\</c>. Server-side
///         <c>WindowsTentacleUpgradeStrategy</c> (future Phase E) will
///         write here.</item>
///   <item><see cref="NullUpgradeStatusStorage"/> — fallback for
///         macOS / other / testing. Returns empty strings — the server
///         treats that as "no status available" and falls back to
///         agent-version-based outcome inference.</item>
/// </list></para>
///
/// <para>All implementations swallow IO errors (returning empty
/// string) — upgrade-status reporting is advisory metadata, not
/// correctness-critical. A broken filesystem must NOT degrade the
/// whole Capabilities RPC (which also carries os/shells/version info
/// needed for deployment script selection).</para>
/// </summary>
public interface IUpgradeStatusStorage
{
    /// <summary>JSON status blob written at upgrade end (status, target version, error if any).</summary>
    string ReadStatus();

    /// <summary>JSONL events log written throughout an upgrade attempt (one entry per phase transition).</summary>
    string ReadEvents();

    /// <summary>Plain-text upgrade log (Phase B bash output / Windows installer log).
    /// Long content is tail-truncated to the cap configured by <c>CapabilitiesService.MaxUpgradeLogBytes</c>
    /// — implementations should pre-truncate to keep RPC payloads bounded.</summary>
    string ReadLog();
}
