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
    /// Canonical location of the on-disk upgrade status file written by
    /// <c>upgrade-linux-tentacle.sh</c>'s <c>write_status</c> helper.
    /// </summary>
    private const string UpgradeStatusFilePath = "/var/lib/squid-tentacle/last-upgrade.json";

    private readonly Dictionary<string, string> _metadata;
    private readonly Func<string> _upgradeStatusReader;

    public CapabilitiesService() : this(metadata: null) { }

    public CapabilitiesService(Dictionary<string, string> metadata) : this(metadata, DefaultUpgradeStatusReader) { }

    /// <summary>
    /// Test-friendly ctor: caller can inject a status-file reader to avoid
    /// touching the real filesystem in unit tests.
    /// </summary>
    internal CapabilitiesService(Dictionary<string, string> metadata, Func<string> upgradeStatusReader)
    {
        _metadata = MergeWithRuntimeCapabilities(metadata);
        _upgradeStatusReader = upgradeStatusReader ?? DefaultUpgradeStatusReader;
    }

    public CapabilitiesResponse GetCapabilities(CapabilitiesRequest request)
    {
        // Fresh clone every call — the per-call upgrade status read below
        // must not pollute the instance-level cached metadata.
        var metadataForThisCall = new Dictionary<string, string>(_metadata);

        var upgradeStatus = _upgradeStatusReader();
        if (!string.IsNullOrEmpty(upgradeStatus))
            metadataForThisCall[UpgradeStatusMetadataKey] = upgradeStatus;

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

    private static Dictionary<string, string> MergeWithRuntimeCapabilities(Dictionary<string, string> overrides)
    {
        var merged = RuntimeCapabilitiesInspector.Inspect();

        if (overrides == null) return merged;

        foreach (var (key, value) in overrides)
            merged[key] = value;

        return merged;
    }
}
