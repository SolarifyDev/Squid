using Squid.Core.Services.Machines.Upgrade;
using Squid.Message.Requests.Machines;

namespace Squid.Core.Services.Machines.Scripts.Tentacle;

/// <summary>
/// Single source of truth for the (OS, RID, archive URL, label,
/// recommended-flag) catalogue of Tentacle downloadable archives. Composes
/// the canonical URL pattern (delegated to each per-OS upgrade strategy's
/// <c>BuildDownloadUrl</c>) with the FE-display fields (label / libc-variant /
/// recommended flag) that are NOT part of the upgrade pipeline's concerns.
///
/// <para>Shared between two consumer sites:
/// <list type="bullet">
///   <item><see cref="Handlers.RequestHandlers.Machines.GetTentacleDownloadsRequestHandler"/>
///         — surfaces the catalogue verbatim through
///         <c>GET /api/machines/tentacle-downloads</c>.</item>
///   <item><see cref="MachineScriptService"/> — bundles the catalogue into the
///         <c>POST /api/machines/generate-tentacle-install-script</c> response
///         alongside the install scripts so the FE can render both UX paths
///         (paste-script + download-link) in one wizard step.</item>
/// </list>
/// </para>
///
/// <para>Lives next to the script builders (not the upgrade strategies) because
/// its concern is FE-facing UX shape, not the upgrade dispatch pipeline.</para>
/// </summary>
internal static class TentacleDownloadCatalog
{
    public static List<TentacleDownloadDto> BuildWindows(string version) =>
    [
        BuildWindowsDto(version, "win-x64",   "x64",   "Windows x64",   isRecommended: true),
        BuildWindowsDto(version, "win-arm64", "arm64", "Windows ARM64", isRecommended: false)
    ];

    public static List<TentacleDownloadDto> BuildLinux(string version) =>
    [
        BuildLinuxDto(version, "linux-x64",        "x64",   libc: "glibc", "Linux x64 (glibc)",         isRecommended: true),
        BuildLinuxDto(version, "linux-arm64",      "arm64", libc: "glibc", "Linux ARM64 (glibc)",       isRecommended: false),
        BuildLinuxDto(version, "linux-musl-x64",   "x64",   libc: "musl",  "Alpine Linux x64 (musl)",   isRecommended: false),
        BuildLinuxDto(version, "linux-musl-arm64", "arm64", libc: "musl",  "Alpine Linux ARM64 (musl)", isRecommended: false)
    ];

    public static bool ShouldIncludeWindows(string osFilter) =>
        osFilter == null || string.Equals(osFilter, "Windows", StringComparison.OrdinalIgnoreCase);

    public static bool ShouldIncludeLinux(string osFilter) =>
        osFilter == null || string.Equals(osFilter, "Linux", StringComparison.OrdinalIgnoreCase);

    public static string NormaliseOsFilter(string raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    /// <summary>
    /// Returns the literal version pin if non-empty/non-"latest", else null
    /// (signalling the caller to query the registry).
    /// </summary>
    public static string NormaliseVersionPin(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        return string.Equals(trimmed, "latest", StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }

    private static TentacleDownloadDto BuildWindowsDto(string version, string rid, string arch, string label, bool isRecommended)
    {
        var url = WindowsTentacleUpgradeStrategy.BuildDownloadUrl(version, rid);
        return new TentacleDownloadDto
        {
            OperatingSystem = "Windows",
            Architecture = arch,
            LibcVariant = null,
            Rid = rid,
            DownloadUrl = url,
            Sha256Url = url + ".sha256",
            FileName = $"squid-tentacle-{version}-{rid}.zip",
            Label = label,
            IsRecommended = isRecommended
        };
    }

    private static TentacleDownloadDto BuildLinuxDto(string version, string rid, string arch, string libc, string label, bool isRecommended)
    {
        var url = LinuxTentacleUpgradeStrategy.BuildDownloadUrl(version, rid);
        return new TentacleDownloadDto
        {
            OperatingSystem = "Linux",
            Architecture = arch,
            LibcVariant = libc,
            Rid = rid,
            DownloadUrl = url,
            Sha256Url = url + ".sha256",
            FileName = $"squid-tentacle-{version}-{rid}.tar.gz",
            Label = label,
            IsRecommended = isRecommended
        };
    }
}
