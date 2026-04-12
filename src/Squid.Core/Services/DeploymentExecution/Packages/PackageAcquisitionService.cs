using Squid.Core.DependencyInjection;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Http;

namespace Squid.Core.Services.DeploymentExecution.Packages;

public class PackageAcquisitionService(IPackageContentFetcher packageContentFetcher) : IPackageAcquisitionService
{
    public async Task<PackageAcquisitionResult> AcquireAsync(ExternalFeed feed, string packageId, string version, int deploymentId, CancellationToken ct)
    {
        var fetchResult = await packageContentFetcher.FetchAsync(feed, packageId, version, ct).ConfigureAwait(false);

        if (fetchResult.Warnings.Count > 0)
            Log.Warning("[Deploy] Package fetch warnings for {PackageId} v{Version}: {Warnings}", packageId, version, string.Join("; ", fetchResult.Warnings));

        if (fetchResult.RawBytes.Length == 0)
            throw new InvalidOperationException($"Package {packageId} v{version} from feed {feed.Id} returned empty content.");

        var storageDir = PackageAcquisitionServiceExtensions.BuildPackageStoragePath(deploymentId);
        Directory.CreateDirectory(storageDir);

        var localPath = Path.Combine(storageDir, $"{packageId}.{version}.nupkg");
        await File.WriteAllBytesAsync(localPath, fetchResult.RawBytes, ct).ConfigureAwait(false);

        var hash = ComputeMd5Hash(fetchResult.RawBytes);

        Log.Information("[Deploy] Package acquired: {PackageId} v{Version} -> {LocalPath} ({SizeBytes} bytes, hash {Hash})", packageId, version, localPath, fetchResult.RawBytes.Length, hash);

        return new PackageAcquisitionResult(localPath, packageId, version, fetchResult.RawBytes.Length, hash);
    }

    private static string ComputeMd5Hash(byte[] bytes)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }
}
