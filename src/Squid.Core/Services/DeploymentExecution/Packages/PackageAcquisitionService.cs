using System.Security.Cryptography;
using Squid.Core.DependencyInjection;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Http;

namespace Squid.Core.Services.DeploymentExecution.Packages;

public class PackageAcquisitionService(IPackageContentFetcher packageContentFetcher) : IPackageAcquisitionService
{
    public async Task<PackageAcquisitionResult> AcquireAsync(ExternalFeed feed, string packageId, string version, int deploymentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            throw new InvalidOperationException("Package ID is required for package acquisition.");
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException($"Package version is required for package '{packageId}'.");
        if (feed is null)
            throw new InvalidOperationException($"Feed is required for package '{packageId}' v{version}.");

        var feedType = feed.FeedType ?? string.Empty;
        if (feedType.Contains("Helm", StringComparison.OrdinalIgnoreCase)
            || feedType.Contains("GitHub", StringComparison.OrdinalIgnoreCase)
            || feedType.Contains("Docker", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Feed {feed.Id} type '{feed.FeedType}' is not a NuGet feed. Deploy a Package V1 only supports external NuGet feeds.");
        }

        var fetchResult = await packageContentFetcher.FetchAsync(feed, packageId, version, ct).ConfigureAwait(false);

        if (fetchResult.Warnings.Count > 0)
            Log.Warning("[Deploy] Package fetch warnings for {PackageId} v{Version}: {Warnings}", packageId, version, string.Join("; ", fetchResult.Warnings));

        if (fetchResult.RawBytes.Length == 0)
            throw new InvalidOperationException($"Package {packageId} v{version} from feed {feed.Id} returned empty content.");

        var storageDir = PackageAcquisitionServiceExtensions.BuildPackageStoragePath(deploymentId);
        Directory.CreateDirectory(storageDir);

        var localPath = Path.Combine(storageDir, $"{packageId}.{version}.nupkg");
        await File.WriteAllBytesAsync(localPath, fetchResult.RawBytes, ct).ConfigureAwait(false);

        var hash = Convert.ToHexString(SHA256.HashData(fetchResult.RawBytes)).ToLowerInvariant();

        Log.Information("[Deploy] Package acquired: {PackageId} v{Version} -> {LocalPath} ({SizeBytes} bytes, hash {Hash})", packageId, version, localPath, fetchResult.RawBytes.Length, hash);

        return new PackageAcquisitionResult(localPath, packageId, version, fetchResult.RawBytes.Length, hash);
    }
}
