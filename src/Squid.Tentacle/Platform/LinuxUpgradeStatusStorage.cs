using Squid.Tentacle.Core;

namespace Squid.Tentacle.Platform;

/// <summary>
/// P1-Phase12.A.2 — Linux upgrade-status storage. Paths are bit-for-bit
/// identical to pre-Phase-12 hardcoded literals in <c>CapabilitiesService</c>.
///
/// <para><b>Why pinned literals matter</b>: <c>upgrade-linux-tentacle.sh</c>
/// (the bash script the server-side <c>LinuxTentacleUpgradeStrategy</c>
/// dispatches via SSH/Halibut) writes to these EXACT paths. Renaming
/// either side silently breaks the upgrade-status reporting feature —
/// server reads "no status" → falls back to version inference → may
/// report success when the upgrade actually failed. Pinned by tests.</para>
/// </summary>
public sealed class LinuxUpgradeStatusStorage : IUpgradeStatusStorage
{
    /// <summary>Pinned: <c>/var/lib/squid-tentacle/last-upgrade.json</c>.</summary>
    public const string StatusFilePath = "/var/lib/squid-tentacle/last-upgrade.json";

    /// <summary>Pinned: <c>/var/lib/squid-tentacle/upgrade-events.jsonl</c>.</summary>
    public const string EventsFilePath = "/var/lib/squid-tentacle/upgrade-events.jsonl";

    /// <summary>Pinned: <c>/var/log/squid-tentacle-upgrade.log</c>.</summary>
    public const string LogFilePath = "/var/log/squid-tentacle-upgrade.log";

    public string ReadStatus() => SafeRead(StatusFilePath);
    public string ReadEvents() => SafeRead(EventsFilePath);

    public string ReadLog()
    {
        try
        {
            if (!File.Exists(LogFilePath)) return string.Empty;
            var bytes = File.ReadAllBytes(LogFilePath);
            return CapabilitiesService.TailTruncateForMetadata(bytes, CapabilitiesService.MaxUpgradeLogBytesValue);
        }
        catch
        {
            // Advisory metadata — broken FS must not degrade Capabilities RPC.
            return string.Empty;
        }
    }

    private static string SafeRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
