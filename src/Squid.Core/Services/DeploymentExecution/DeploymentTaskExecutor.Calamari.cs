using System.IO.Compression;
using Squid.Core.Services.Common;

namespace Squid.Core.Services.DeploymentExecution;

public partial class DeploymentTaskExecutor
{
    private async Task PrepareCalamariIfRequiredAsync(CancellationToken ct)
    {
        var needsCalamari = _ctx.AllTargetsContext.Any(tc => tc.ResolvedStrategy != null);

        if (!needsCalamari) return;

        _ctx.CalamariPackageBytes = await DownloadCalamariPackageAsync().ConfigureAwait(false);

        Log.Information("Calamari package downloaded for deployment {DeploymentId}", _ctx.Deployment.Id);
    }

    private async Task<byte[]> DownloadCalamariPackageAsync()
    {
        const string packageId = "Calamari";
        const string githubUserName = "SolarifyDev";

        var version = _calamariGithubPackageSetting.Version;

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

        var bytes = ReadAllBytes(packageStream);

        await File.WriteAllBytesAsync(cacheFilePath, bytes).ConfigureAwait(false);

        Log.Information("Downloaded and cached Calamari package to {CachePath}", cacheFilePath);

        return bytes;
    }

    private byte[] CreateYamlNuGetPackage(Dictionary<string, Stream> yamlStreams)
    {
        if (yamlStreams == null || yamlStreams.Count == 0)
        {
            Log.Information("No YAML streams to pack into NuGet package");
            return Array.Empty<byte>();
        }

        return _yamlNuGetPacker.CreateNuGetPackageFromYamlStreams(yamlStreams);
    }

    private void CheckNugetPackage(byte[] packageBytes)
    {
        if (packageBytes == null || packageBytes.Length == 0) return;

        using var stream = new MemoryStream(packageBytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        Log.Information("=== NuGet Package Contents ===");
        Log.Information("Total entries in package: {Count}", archive.Entries.Count);

        foreach (var entry in archive.Entries)
        {
            Log.Information("Entry: {EntryName}, Size: {Size} bytes", entry.FullName, entry.Length);

            if (entry.FullName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            {
                using var entryStream = entry.Open();
                using var reader = new StreamReader(entryStream);
                var content = reader.ReadToEnd();
                Log.Information("=== {FileName} ===\n{Content}", entry.FullName, content);
            }
        }

        Log.Information("=== End of Package Contents ===");
    }

    private string GetCalamariVersion()
    {
        return string.IsNullOrWhiteSpace(_calamariGithubPackageSetting.Version) ? "28.2.1" : _calamariGithubPackageSetting.Version;
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        if (stream == null) return Array.Empty<byte>();

        if (stream.CanSeek) stream.Position = 0;

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
