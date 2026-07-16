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
        if (IsUnsupportedPackageFeed(feedType))
        {
            throw new InvalidOperationException(
                $"Feed type '{feed.FeedType}' cannot be installed by Deploy a Package. Use an archive-capable feed (NuGet/GitHub/HTTP).");
        }

        var fetchResult = await packageContentFetcher.FetchAsync(feed, packageId, version, ct).ConfigureAwait(false);

        if (fetchResult.Warnings.Count > 0)
            Log.Warning("[Deploy] Package fetch warnings for {PackageId} v{Version}: {Warnings}", packageId, version, string.Join("; ", fetchResult.Warnings));

        if (fetchResult.RawBytes.Length == 0)
            throw new InvalidOperationException($"Package {packageId} v{version} from feed {feed.Id} returned empty content.");

        var storageDir = PackageAcquisitionServiceExtensions.BuildPackageStoragePath(deploymentId);
        Directory.CreateDirectory(storageDir);

        var extension = ResolveArchiveExtension(feedType, packageId, feed.FeedUri);
        var safePackageId = SanitizeFileSegment(packageId);
        var safeVersion = SanitizeFileSegment(version);
        var localPath = Path.Combine(storageDir, $"{safePackageId}.{safeVersion}{extension}");
        await File.WriteAllBytesAsync(localPath, fetchResult.RawBytes, ct).ConfigureAwait(false);

        var hash = Convert.ToHexString(SHA256.HashData(fetchResult.RawBytes)).ToLowerInvariant();

        Log.Information("[Deploy] Package acquired: {PackageId} v{Version} -> {LocalPath} ({SizeBytes} bytes, hash {Hash})", packageId, version, localPath, fetchResult.RawBytes.Length, hash);

        return new PackageAcquisitionResult(localPath, packageId, version, fetchResult.RawBytes.Length, hash);
    }

    internal static bool IsUnsupportedPackageFeed(string feedType)
    {
        if (string.IsNullOrWhiteSpace(feedType))
            return false;

        return feedType.Contains("Docker", StringComparison.OrdinalIgnoreCase)
            || feedType.Contains("Helm", StringComparison.OrdinalIgnoreCase)
            || feedType.Contains("Container", StringComparison.OrdinalIgnoreCase);
    }

    internal static string ResolveArchiveExtension(string feedType, string packageId, string feedUri)
    {
        var fromPackageId = InferExtensionFromPath(packageId);
        if (fromPackageId != null)
            return fromPackageId;

        if (!string.IsNullOrWhiteSpace(feedType) && feedType.Contains("NuGet", StringComparison.OrdinalIgnoreCase))
            return ".nupkg";

        if (!string.IsNullOrWhiteSpace(feedType) && feedType.Contains("GitHub", StringComparison.OrdinalIgnoreCase))
            return ".tar.gz";

        // Only honor feedUri when the last path segment looks like a package file
        // (e.g. .../download/app.nupkg). Generic feed base URIs must not override.
        var fromUri = InferExtensionFromPackageFilePath(feedUri);
        if (fromUri != null)
            return fromUri;

        return ".zip";
    }

    private static string InferExtensionFromPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var path = value;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            path = uri.AbsolutePath;

        if (path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            return path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ? ".tgz" : ".tar.gz";

        if (path.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
            return ".nupkg";

        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return ".zip";

        if (path.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            return ".tar";

        return null;
    }

    private static string InferExtensionFromPackageFilePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var path = value;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            path = uri.AbsolutePath;

        path = path.TrimEnd('/');
        var lastSlash = path.LastIndexOf('/');
        var fileName = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOf('.') < 0)
            return null;

        return InferExtensionFromPath(fileName);
    }

    private static string SanitizeFileSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "package";

        // Align with SSH remote segment rules so acquired local names match remote staging names.
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c =>
            invalid.Contains(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'
                ? '_'
                : c).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrEmpty(sanitized) ? "package" : sanitized;
    }
}
