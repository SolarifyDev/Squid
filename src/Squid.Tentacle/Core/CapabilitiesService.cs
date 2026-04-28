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
    /// P1-Phase12.A.2 — public-internal exposure for
    /// <see cref="Squid.Tentacle.Platform.IUpgradeStatusStorage"/> impls
    /// that share the same byte cap as this service's metadata response.
    /// Drift between cap values would mean storage truncates differently
    /// than the service expects; centralised constant prevents that.
    /// </summary>
    public static int MaxUpgradeLogBytesValue => MaxUpgradeLogBytes;

    private readonly Dictionary<string, string> _metadata;
    private readonly Squid.Tentacle.Platform.IUpgradeStatusStorage _upgradeStorage;

    public CapabilitiesService() : this(metadata: null) { }

    public CapabilitiesService(Dictionary<string, string> metadata)
        : this(metadata, Squid.Tentacle.Platform.UpgradeStatusStorageFactory.Resolve()) { }

    /// <summary>
    /// P1-Phase12.A.2 — test-friendly ctor accepting a custom
    /// <see cref="Squid.Tentacle.Platform.IUpgradeStatusStorage"/>.
    /// Replaces the pre-Phase-12 three-Func injection point with a
    /// single typed contract — same testability, cleaner surface.
    /// Default implementations live at:
    ///   <list type="bullet">
    ///     <item><see cref="Squid.Tentacle.Platform.LinuxUpgradeStatusStorage"/></item>
    ///     <item><see cref="Squid.Tentacle.Platform.WindowsUpgradeStatusStorage"/></item>
    ///     <item><see cref="Squid.Tentacle.Platform.NullUpgradeStatusStorage"/> (fallback)</item>
    ///   </list>
    /// </summary>
    public CapabilitiesService(Dictionary<string, string> metadata,
        Squid.Tentacle.Platform.IUpgradeStatusStorage upgradeStorage)
    {
        _metadata = MergeWithRuntimeCapabilities(metadata);
        _upgradeStorage = upgradeStorage ?? new Squid.Tentacle.Platform.NullUpgradeStatusStorage();
    }

    public CapabilitiesResponse GetCapabilities(CapabilitiesRequest request)
    {
        // Fresh clone every call — the per-call upgrade status read below
        // must not pollute the instance-level cached metadata.
        var metadataForThisCall = new Dictionary<string, string>(_metadata);

        var upgradeStatus = _upgradeStorage.ReadStatus();
        if (!string.IsNullOrEmpty(upgradeStatus))
            metadataForThisCall[UpgradeStatusMetadataKey] = upgradeStatus;

        var upgradeEvents = _upgradeStorage.ReadEvents();
        if (!string.IsNullOrEmpty(upgradeEvents))
            metadataForThisCall[UpgradeEventsMetadataKey] = upgradeEvents;

        var upgradeLog = _upgradeStorage.ReadLog();
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
    /// Pure byte-to-string tail-truncation with UTF-8 boundary safety.
    /// Extracted for direct unit testing; the file-reading wrapper above
    /// handles IO concerns. <c>public</c> so platform-specific
    /// <see cref="Squid.Tentacle.Platform.IUpgradeStatusStorage"/> impls
    /// can share the same truncation policy.
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
    public static string TailTruncateForMetadata(byte[] bytes, int maxBytes)
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

        // P0-Phase10.1 (audit C.3): merge K8s RBAC dry-run results so the
        // server-side KubernetesAgentHealthCheckStrategy can fail-fast on
        // a permission-revoked agent BEFORE the first deploy fails with a
        // cryptic kubectl Forbidden error. No-op outside K8s pods (detected
        // via KUBERNETES_SERVICE_HOST env var inside the inspector).
        foreach (var (key, value) in KubernetesRbacInspector.Inspect())
            merged[key] = value;

        if (overrides == null) return merged;

        foreach (var (key, value) in overrides)
            merged[key] = value;

        return merged;
    }
}
