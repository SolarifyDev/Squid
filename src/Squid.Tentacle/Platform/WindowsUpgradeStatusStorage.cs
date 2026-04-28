using System.Runtime.Versioning;
using Squid.Tentacle.Core;

namespace Squid.Tentacle.Platform;

/// <summary>
/// P1-Phase12.A.2 — Windows upgrade-status storage. Paths under
/// <c>%PROGRAMDATA%\Squid\Tentacle\upgrade\</c>.
///
/// <para><b>Forward-compat with future Squid.Tentacle.Upgrader.exe</b>:
/// when the Windows MSI-based upgrader (Phase E) ships, it will write
/// to the EXACT paths constructed here. Pin the layout via test so the
/// upgrader and the Capabilities-emit side don't drift.</para>
///
/// <para>Sub-paths exposed as constants (joined with the resolved
/// system config dir at runtime) so tests can pin layout without
/// running on Windows.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsUpgradeStatusStorage : IUpgradeStatusStorage
{
    /// <summary>Sub-path under <c>PlatformPaths.GetSystemConfigDir()</c>: <c>upgrade\last-upgrade.json</c>.</summary>
    public const string StatusFileSubPath = @"upgrade\last-upgrade.json";

    /// <summary>Sub-path: <c>upgrade\upgrade-events.jsonl</c>.</summary>
    public const string EventsFileSubPath = @"upgrade\upgrade-events.jsonl";

    /// <summary>Sub-path: <c>upgrade\upgrade.log</c>.</summary>
    public const string LogFileSubPath = @"upgrade\upgrade.log";

    private string StatusPath => Path.Combine(PlatformPaths.GetSystemConfigDir(), StatusFileSubPath);
    private string EventsPath => Path.Combine(PlatformPaths.GetSystemConfigDir(), EventsFileSubPath);
    private string LogPath => Path.Combine(PlatformPaths.GetSystemConfigDir(), LogFileSubPath);

    public string ReadStatus() => SafeRead(StatusPath);
    public string ReadEvents() => SafeRead(EventsPath);

    public string ReadLog()
    {
        try
        {
            var path = LogPath;
            if (!File.Exists(path)) return string.Empty;
            var bytes = File.ReadAllBytes(path);
            return CapabilitiesService.TailTruncateForMetadata(bytes, CapabilitiesService.MaxUpgradeLogBytesValue);
        }
        catch
        {
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
