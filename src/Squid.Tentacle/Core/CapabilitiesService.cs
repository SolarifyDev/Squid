using System.IO;
using Squid.Message.Contracts.Tentacle;

namespace Squid.Tentacle.Core;

public class CapabilitiesService : ICapabilitiesService
{
    /// <summary>
    /// Key under which the raw upgrade-status JSON is exposed in the
    /// <c>Metadata</c> dictionary of <see cref="CapabilitiesResponse"/>.
    /// Server reads this per health check to detect stale IN_PROGRESS
    /// states (A2, 1.5.0) and auto-clear server-side stuck ServerTasks
    /// and Redis locks. Absent when no upgrade has ever been attempted.
    /// </summary>
    public const string UpgradeStatusMetadataKey = "upgradeStatus";

    /// <summary>
    /// Key under which the upgrade events JSONL log is exposed in the
    /// <c>Metadata</c> dictionary (B1, 1.5.0). Contents: one JSON object
    /// per line, each describing a key transition during the upgrade
    /// (start, method-try, scope-exec, restart-start, healthz-pass,
    /// success, etc.). Server streams these to the UI task activity log
    /// so operators see real-time progress instead of the "silent for
    /// 20 seconds while scope restarts" gap. Absent when no upgrade
    /// events have ever been emitted.
    /// </summary>
    public const string UpgradeEventsMetadataKey = "upgradeEvents";

    /// <summary>
    /// Key under which the Phase B on-disk log
    /// (<c>/var/log/squid-tentacle-upgrade.log</c>) is embedded in the
    /// Capabilities metadata (B4, 1.6.0). Contents: full text of the
    /// most recent Phase B log — `systemctl restart` output, healthz
    /// verification, rollback details. Server exposes this verbatim
    /// via <c>GET /api/machine/{id}/upgrade-log</c> so operators can
    /// pull the full log without SSHing to the agent.
    ///
    /// <para>Truncation: Phase B truncates the on-disk file at its
    /// start, so this only carries the CURRENT run's output (typically
    /// 1-5KB, small enough to ship on every Capabilities RPC). If the
    /// file somehow exceeds <see cref="MaxUpgradeLogBytes"/>, we
    /// truncate the head and prepend a marker so server sees the most
    /// recent output — the tail is what matters for debugging.</para>
    /// </summary>
    public const string UpgradeLogMetadataKey = "upgradeLog";

    /// <summary>
    /// Hard cap on the upgrade-log content carried in Capabilities metadata.
    /// Typical Phase B logs are &lt; 2 KB; 50 KB gives ~10x safety margin
    /// for rollback-heavy scenarios without ballooning the RPC response.
    /// Beyond this, the log is tail-truncated (head dropped) so server
    /// always sees the most recent output.
    /// </summary>
    internal const int MaxUpgradeLogBytes = 50_000;

    /// <summary>
    /// Canonical location of the on-disk upgrade status file written by
    /// <c>upgrade-linux-tentacle.sh</c>'s <c>write_status</c> helper.
    /// </summary>
    private const string UpgradeStatusFilePath = "/var/lib/squid-tentacle/last-upgrade.json";

    /// <summary>
    /// JSONL event log written by <c>upgrade-linux-tentacle.sh</c>'s
    /// <c>emit_event</c> helper — append-only for the duration of one
    /// upgrade attempt, truncated at Phase A start of the next attempt.
    /// </summary>
    private const string UpgradeEventsFilePath = "/var/lib/squid-tentacle/upgrade-events.jsonl";

    /// <summary>
    /// Phase B bash log. Truncated at Phase B entry so this only carries
    /// the CURRENT run's output.
    /// </summary>
    private const string UpgradeLogFilePath = "/var/log/squid-tentacle-upgrade.log";

    private readonly Dictionary<string, string> _metadata;
    private readonly Func<string> _upgradeStatusReader;
    private readonly Func<string> _upgradeEventsReader;
    private readonly Func<string> _upgradeLogReader;

    public CapabilitiesService() : this(metadata: null) { }

    public CapabilitiesService(Dictionary<string, string> metadata)
        : this(metadata, DefaultUpgradeStatusReader, DefaultUpgradeEventsReader, DefaultUpgradeLogReader) { }

    /// <summary>
    /// Test-friendly ctor: caller can inject all three upgrade-file
    /// readers to avoid touching the real filesystem in unit tests.
    /// </summary>
    internal CapabilitiesService(Dictionary<string, string> metadata,
        Func<string> upgradeStatusReader,
        Func<string> upgradeEventsReader = null,
        Func<string> upgradeLogReader = null)
    {
        _metadata = MergeWithRuntimeCapabilities(metadata);
        _upgradeStatusReader = upgradeStatusReader ?? DefaultUpgradeStatusReader;
        _upgradeEventsReader = upgradeEventsReader ?? DefaultUpgradeEventsReader;
        _upgradeLogReader = upgradeLogReader ?? DefaultUpgradeLogReader;
    }

    public CapabilitiesResponse GetCapabilities(CapabilitiesRequest request)
    {
        // Fresh clone every call — the per-call upgrade status read below
        // must not pollute the instance-level cached metadata.
        var metadataForThisCall = new Dictionary<string, string>(_metadata);

        var upgradeStatus = _upgradeStatusReader();
        if (!string.IsNullOrEmpty(upgradeStatus))
            metadataForThisCall[UpgradeStatusMetadataKey] = upgradeStatus;

        var upgradeEvents = _upgradeEventsReader();
        if (!string.IsNullOrEmpty(upgradeEvents))
            metadataForThisCall[UpgradeEventsMetadataKey] = upgradeEvents;

        var upgradeLog = _upgradeLogReader();
        if (!string.IsNullOrEmpty(upgradeLog))
            metadataForThisCall[UpgradeLogMetadataKey] = upgradeLog;

        return new CapabilitiesResponse
        {
            SupportedServices = new List<string> { "IScriptService/v1", "ICapabilitiesService/v1" },
            AgentVersion = AssemblyVersion.Canonical,
            Metadata = metadataForThisCall
        };
    }

    /// <summary>
    /// Read the on-disk upgrade status file. Returns an empty string if the
    /// file doesn't exist (no upgrade ever attempted) OR any IO error
    /// occurs (permission, mid-write race, etc.). The server treats empty
    /// as "no status available" and falls back to inferring outcome from
    /// the reported agent version.
    /// </summary>
    private static string DefaultUpgradeStatusReader()
    {
        try
        {
            return File.Exists(UpgradeStatusFilePath)
                ? File.ReadAllText(UpgradeStatusFilePath)
                : string.Empty;
        }
        catch
        {
            // Intentional: status file is advisory, not critical. Swallow
            // any error so a broken filesystem can't degrade the whole
            // Capabilities RPC (which also carries os/shells/version info
            // needed for deployment script selection).
            return string.Empty;
        }
    }

    /// <summary>
    /// Read the JSONL events file for the current/last upgrade. Returns
    /// empty string if no file (no upgrade ever run) or any IO error.
    /// Same swallow-errors principle as the status reader.
    /// </summary>
    private static string DefaultUpgradeEventsReader()
    {
        try
        {
            return File.Exists(UpgradeEventsFilePath)
                ? File.ReadAllText(UpgradeEventsFilePath)
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Read the Phase B log file for the current/last upgrade. Returns
    /// empty on missing / IO error. Tail-truncates the content to
    /// <see cref="MaxUpgradeLogBytes"/> — keeps the most recent output
    /// visible to operators (the end of a log is where
    /// rollback/failure details live).
    /// </summary>
    private static string DefaultUpgradeLogReader()
    {
        try
        {
            if (!File.Exists(UpgradeLogFilePath)) return string.Empty;

            var bytes = File.ReadAllBytes(UpgradeLogFilePath);
            return TailTruncateForMetadata(bytes, MaxUpgradeLogBytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Pure byte-to-string tail-truncation with UTF-8 boundary safety.
    /// Extracted for direct unit testing; the file-reading wrapper above
    /// handles IO concerns.
    ///
    /// <para><b>UTF-8 boundary safety (audit D.3):</b> the raw byte-
    /// offset cut could land mid-character. A UTF-8 continuation byte has
    /// top bits <c>10</c> (mask <c>0xC0</c> → <c>0x80</c>); a lead byte
    /// has <c>0</c>, <c>110</c>, <c>1110</c>, or <c>11110</c> (mask
    /// <c>0xC0</c> → NOT <c>0x80</c>). Advance <c>startByte</c> forward
    /// over any continuation bytes until we find a lead byte (or reach
    /// end of array). Result: the returned string is guaranteed to
    /// decode cleanly; worst case we drop up to 3 extra bytes at the
    /// head (max UTF-8 sequence length minus 1).</para>
    /// </summary>
    internal static string TailTruncateForMetadata(byte[] bytes, int maxBytes)
    {
        if (bytes == null || bytes.Length == 0) return string.Empty;

        if (bytes.Length <= maxBytes)
            return System.Text.Encoding.UTF8.GetString(bytes);

        var startByte = bytes.Length - (maxBytes - 128);  // leave room for marker
        while (startByte < bytes.Length && (bytes[startByte] & 0xC0) == 0x80)
        {
            startByte++;
        }
        var keep = bytes.Length - startByte;
        var tail = System.Text.Encoding.UTF8.GetString(bytes, startByte, keep);
        return $"[…{startByte} earlier bytes truncated by CapabilitiesService cap ({maxBytes})…]\n{tail}";
    }

    private static Dictionary<string, string> MergeWithRuntimeCapabilities(Dictionary<string, string> overrides)
    {
        var merged = RuntimeCapabilitiesInspector.Inspect();

        if (overrides == null) return merged;

        foreach (var (key, value) in overrides)
            merged[key] = value;

        return merged;
    }
}
