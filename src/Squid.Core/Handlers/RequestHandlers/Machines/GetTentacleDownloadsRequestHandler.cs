using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.Machines.Scripts.Tentacle;
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
/// <para><b>Composition:</b> the per-OS DTO build + URL pattern lives in
/// <see cref="TentacleDownloadCatalog"/>, shared with
/// <c>MachineScriptService</c> so the install-script generator response
/// and this endpoint produce bit-identical download lists. Single source
/// of truth for the catalogue shape; this handler is a thin adapter that
/// adds version resolution + the response envelope.</para>
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
        var os = TentacleDownloadCatalog.NormaliseOsFilter(msg.OperatingSystem);
        var pinnedVersion = TentacleDownloadCatalog.NormaliseVersionPin(msg.Version);

        var windowsVersion = TentacleDownloadCatalog.ShouldIncludeWindows(os)
            ? pinnedVersion ?? await ResolveWindowsLatestAsync(cancellationToken).ConfigureAwait(false)
            : null;

        var linuxVersion = TentacleDownloadCatalog.ShouldIncludeLinux(os)
            ? pinnedVersion ?? await ResolveLinuxLatestAsync(cancellationToken).ConfigureAwait(false)
            : null;

        var downloads = new List<TentacleDownloadDto>();

        if (windowsVersion != null) downloads.AddRange(TentacleDownloadCatalog.BuildWindows(windowsVersion));
        if (linuxVersion != null) downloads.AddRange(TentacleDownloadCatalog.BuildLinux(linuxVersion));

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
}
