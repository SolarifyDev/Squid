using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.Machines.Upgrade;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Requests.Machines;

namespace Squid.Core.Handlers.RequestHandlers.Machines;

/// <summary>
/// Surfaces the canonical Tentacle archive download URLs for the FE's
/// "Add Tentacle → choose download" UX. Pure projection — no DB hit, no
/// remote side effects beyond the at-most-two version queries that the
/// registry handles transparently (Docker Hub for Linux, GitHub Releases
/// for Windows; both with TTL caches).
///
/// <para><b>Composition:</b> the URL pattern lives on each per-OS upgrade
/// strategy (<c>WindowsTentacleUpgradeStrategy.BuildDownloadUrl</c> /
/// <c>LinuxTentacleUpgradeStrategy.BuildDownloadUrl</c>) — single source of
/// truth, already pinned by upgrade-pipeline tests. This handler stays a
/// thin adapter that calls those internal helpers + adds the FE-display
/// fields (label, recommended flag, libc variant).</para>
/// </summary>
public class GetTentacleDownloadsRequestHandler : IRequestHandler<GetTentacleDownloadsRequest, GetTentacleDownloadsResponse>
{
    private readonly ITentacleVersionRegistry _versionRegistry;

    public GetTentacleDownloadsRequestHandler(ITentacleVersionRegistry versionRegistry)
    {
        _versionRegistry = versionRegistry;
    }

    public async Task<GetTentacleDownloadsResponse> Handle(IReceiveContext<GetTentacleDownloadsRequest> context, CancellationToken cancellationToken)
    {
        var msg = context.Message;
        var os = NormaliseOsFilter(msg.OperatingSystem);
        var pinnedVersion = NormaliseVersionPin(msg.Version);

        var windowsVersion = ShouldIncludeWindows(os)
            ? pinnedVersion ?? await ResolveWindowsLatestAsync(cancellationToken).ConfigureAwait(false)
            : null;

        var linuxVersion = ShouldIncludeLinux(os)
            ? pinnedVersion ?? await ResolveLinuxLatestAsync(cancellationToken).ConfigureAwait(false)
            : null;

        var downloads = new List<TentacleDownloadDto>();

        if (windowsVersion != null) downloads.AddRange(BuildWindowsDownloads(windowsVersion));
        if (linuxVersion != null) downloads.AddRange(BuildLinuxDownloads(linuxVersion));

        return new GetTentacleDownloadsResponse
        {
            Data = new GetTentacleDownloadsResponseData
            {
                LatestVersion = pinnedVersion ?? windowsVersion ?? linuxVersion ?? string.Empty,
                Downloads = downloads
            }
        };
    }

    private Task<string> ResolveWindowsLatestAsync(CancellationToken ct) =>
        _versionRegistry.GetLatestVersionAsync(
            nameof(CommunicationStyle.TentaclePolling),
            new MachineRuntimeCapabilities { Os = AgentOperatingSystems.Windows },
            ct);

    private Task<string> ResolveLinuxLatestAsync(CancellationToken ct) =>
        _versionRegistry.GetLatestVersionAsync(
            nameof(CommunicationStyle.TentaclePolling),
            new MachineRuntimeCapabilities { Os = AgentOperatingSystems.Linux },
            ct);

    private static string NormaliseOsFilter(string raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    private static string NormaliseVersionPin(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        return string.Equals(trimmed, "latest", StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }

    private static bool ShouldIncludeWindows(string osFilter) =>
        osFilter == null || string.Equals(osFilter, "Windows", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldIncludeLinux(string osFilter) =>
        osFilter == null || string.Equals(osFilter, "Linux", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<TentacleDownloadDto> BuildWindowsDownloads(string version) =>
    [
        BuildWindowsDto(version, "win-x64", "x64", "Windows x64", isRecommended: true),
        BuildWindowsDto(version, "win-arm64", "arm64", "Windows ARM64", isRecommended: false)
    ];

    private static IEnumerable<TentacleDownloadDto> BuildLinuxDownloads(string version) =>
    [
        BuildLinuxDto(version, "linux-x64",       "x64",   libc: "glibc", "Linux x64 (glibc)",          isRecommended: true),
        BuildLinuxDto(version, "linux-arm64",     "arm64", libc: "glibc", "Linux ARM64 (glibc)",        isRecommended: false),
        BuildLinuxDto(version, "linux-musl-x64",  "x64",   libc: "musl",  "Alpine Linux x64 (musl)",    isRecommended: false),
        BuildLinuxDto(version, "linux-musl-arm64","arm64", libc: "musl",  "Alpine Linux ARM64 (musl)",  isRecommended: false)
    ];

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
