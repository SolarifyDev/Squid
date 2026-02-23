using Squid.Core.Services.Common;
using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution;

public partial class DeploymentTaskExecutor
{
    private async Task PrepareCalamariIfRequiredAsync(CancellationToken ct)
    {
        // KubernetesAgent targets use squid-calamari bundled in the Tentacle image — no download required.
        // Only KubernetesApi targets need the Octopus Calamari package downloaded at runtime.
        var needsCalamari = _ctx.AllTargetsContext.Any(tc =>
            tc.Transport != null &&
            tc.CommunicationStyle != CommunicationStyle.KubernetesAgent);

        if (!needsCalamari) return;

        _ctx.CalamariPackageBytes = await DownloadCalamariPackageAsync().ConfigureAwait(false);

        Log.Information("Calamari package downloaded for deployment {DeploymentId}", _ctx.Deployment.Id);
    }

    private async Task<byte[]> DownloadCalamariPackageAsync()
    {
        const string packageId = "Calamari";
        const string githubUserName = "SolarifyDev";

        var version = _calamariGithubPackageSetting.ResolvedVersion;

        var cacheDirectory = string.IsNullOrWhiteSpace(_calamariGithubPackageSetting.CacheDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "CalamariPackages")
            : _calamariGithubPackageSetting.CacheDirectory;

        Directory.CreateDirectory(cacheDirectory);

        var cacheFilePath = Path.Combine(cacheDirectory, $"Calamari.{version}.nupkg");

        if (File.Exists(cacheFilePath))
        {
            Log.Information("Using cached Calamari package from {CachePath}", cacheFilePath);
            return await File.ReadAllBytesAsync(cacheFilePath).ConfigureAwait(false);
        }

        var downloader = new GithubPackageDownloader(githubUserName, _calamariGithubPackageSetting.Token, _calamariGithubPackageSetting.MirrorUrlTemplate);
        var packageStream = await downloader.DownloadPackageAsync(packageId, version).ConfigureAwait(false);
        using var memory = new MemoryStream();
        await packageStream.CopyToAsync(memory).ConfigureAwait(false);
        var bytes = memory.ToArray();

        await File.WriteAllBytesAsync(cacheFilePath, bytes).ConfigureAwait(false);

        Log.Information("Downloaded and cached Calamari package to {CachePath}", cacheFilePath);

        return bytes;
    }

}
