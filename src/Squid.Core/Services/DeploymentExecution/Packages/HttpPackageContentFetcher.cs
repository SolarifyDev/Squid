using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Serilog;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Http;

namespace Squid.Core.Services.DeploymentExecution.Packages;

public class HttpPackageContentFetcher : IPackageContentFetcher
{
    private static readonly HashSet<string> YamlExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".yaml", ".yml", ".json" };

    private readonly ISquidHttpClientFactory _httpClientFactory;

    public HttpPackageContentFetcher(ISquidHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<PackageFetchResult> FetchAsync(ExternalFeed feed, string packageId, string version, CancellationToken ct)
    {
        var warnings = new List<string>();

        try
        {
            var url = await ResolveDownloadUrlAsync(feed, packageId, version, ct).ConfigureAwait(false);
            var headers = BuildAuthHeaders(feed);
            var client = _httpClientFactory.CreateClient(timeout: TimeSpan.FromMinutes(5), headers: headers);

            Log.Information("[Deploy] Downloading package from {Url}", url);
            var response = await client.GetAsync(url, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                warnings.Add($"Package download failed: HTTP {(int)response.StatusCode} from {url}");
                return new PackageFetchResult(new Dictionary<string, byte[]>(), warnings, Array.Empty<byte>());
            }

            var archiveBytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var files = ExtractArchive(archiveBytes);

            Log.Information("[Deploy] Extracted {FileCount} files from package", files.Count);
            return new PackageFetchResult(files, warnings, archiveBytes);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Deploy] Failed to fetch package {PackageId} v{Version} from feed {FeedId}", packageId, version, feed.Id);
            warnings.Add($"Package fetch failed: {ex.Message}");
            return new PackageFetchResult(new Dictionary<string, byte[]>(), warnings, Array.Empty<byte>());
        }
    }

    private async Task<string> ResolveDownloadUrlAsync(ExternalFeed feed, string packageId, string version, CancellationToken ct)
    {
        var feedType = feed.FeedType ?? string.Empty;

        if (feedType.Contains("Helm", StringComparison.OrdinalIgnoreCase))
            return await ResolveHelmChartUrlAsync(feed, packageId, version, ct).ConfigureAwait(false);

        return BuildDownloadUrl(feed, packageId, version);
    }

    private async Task<string> ResolveHelmChartUrlAsync(ExternalFeed feed, string packageId, string version, CancellationToken ct)
    {
        var baseUri = (feed.FeedUri ?? string.Empty).TrimEnd('/');
        var indexUrl = $"{baseUri}/index.yaml";

        try
        {
            var headers = BuildAuthHeaders(feed);
            var client = _httpClientFactory.CreateClient(timeout: TimeSpan.FromSeconds(30), headers: headers);
            var response = await client.GetAsync(indexUrl, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var indexContent = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var chartUrl = ParseHelmIndexForChartUrl(indexContent, packageId, version, baseUri);

                if (chartUrl != null)
                    return chartUrl;

                Log.Warning("[Deploy] Chart {PackageId} v{Version} not found in Helm index, falling back to convention URL", packageId, version);
            }
            else
            {
                Log.Warning("[Deploy] Failed to fetch Helm index.yaml: HTTP {StatusCode}, falling back to convention URL", (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Deploy] Failed to parse Helm index.yaml, falling back to convention URL");
        }

        return $"{baseUri}/{packageId}-{version}.tgz";
    }

    internal static string ParseHelmIndexForChartUrl(string indexYaml, string packageId, string version, string baseUri)
    {
        // Helm index.yaml fields (version, urls) can appear in any order within an entry.
        // We collect each entry as a block, then independently extract version + url.
        var lines = indexYaml.Split('\n');
        var entryBlocks = ExtractChartEntryBlocks(lines, packageId);

        foreach (var block in entryBlocks)
        {
            var url = MatchEntryVersionAndUrl(block, version, baseUri);

            if (url != null)
                return url;
        }

        return null;
    }

    private static List<List<string>> ExtractChartEntryBlocks(string[] lines, string packageId)
    {
        var blocks = new List<List<string>>();
        var inTargetChart = false;
        var entryIndent = -1;
        List<string> currentBlock = null;

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();

            if (!inTargetChart)
            {
                var stripped = trimmed.TrimStart();

                if (stripped.Equals($"{packageId}:", StringComparison.OrdinalIgnoreCase)
                    || stripped.Equals($"\"{packageId}\":", StringComparison.OrdinalIgnoreCase))
                    inTargetChart = true;

                continue;
            }

            if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.StartsWith(" ") && !trimmed.StartsWith("\t"))
                break;

            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            var indent = trimmed.Length - trimmed.TrimStart().Length;
            var content = trimmed.TrimStart();

            if (content.StartsWith("- ") && entryIndent < 0)
                entryIndent = indent;

            if (content.StartsWith("- ") && indent == entryIndent)
            {
                currentBlock = new List<string>();
                blocks.Add(currentBlock);
            }

            currentBlock?.Add(content);
        }

        return blocks;
    }

    private static string MatchEntryVersionAndUrl(List<string> entryLines, string targetVersion, string baseUri)
    {
        string entryVersion = null;
        string entryUrl = null;
        var inUrls = false;

        foreach (var line in entryLines)
        {
            if (line.StartsWith("- version:", StringComparison.OrdinalIgnoreCase))
            {
                entryVersion = line["- version:".Length..].Trim().Trim('"', '\'');
                inUrls = false;
                continue;
            }

            if (line.StartsWith("version:", StringComparison.OrdinalIgnoreCase) && !line.StartsWith("versions:", StringComparison.OrdinalIgnoreCase))
            {
                entryVersion = line["version:".Length..].Trim().Trim('"', '\'');
                inUrls = false;
                continue;
            }

            if (line.StartsWith("urls:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("- urls:", StringComparison.OrdinalIgnoreCase))
            {
                inUrls = true;
                continue;
            }

            if (inUrls && line.StartsWith("- ") && entryUrl == null)
            {
                entryUrl = line[2..].Trim().Trim('"', '\'');
                inUrls = false;
                continue;
            }

            if (inUrls && !line.StartsWith("- "))
                inUrls = false;
        }

        if (entryVersion == null || !string.Equals(entryVersion, targetVersion, StringComparison.OrdinalIgnoreCase))
            return null;

        if (entryUrl == null) return null;

        if (entryUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || entryUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return entryUrl;

        return $"{baseUri.TrimEnd('/')}/{entryUrl}";
    }

    internal static string BuildDownloadUrl(ExternalFeed feed, string packageId, string version)
    {
        var baseUri = (feed.FeedUri ?? string.Empty).TrimEnd('/');
        var feedType = feed.FeedType ?? string.Empty;

        if (feedType.Contains("GitHub", StringComparison.OrdinalIgnoreCase))
            return $"{baseUri}/repos/{packageId}/tarball/{version}";

        if (feedType.Contains("NuGet", StringComparison.OrdinalIgnoreCase))
            return $"{baseUri}/api/v2/package/{packageId}/{version}";

        if (string.IsNullOrEmpty(version))
            return $"{baseUri}/{packageId}";

        return $"{baseUri}/{packageId}/{version}";
    }

    internal static Dictionary<string, string> BuildAuthHeaders(ExternalFeed feed)
    {
        var headers = new Dictionary<string, string>();
        var feedType = feed.FeedType ?? string.Empty;

        if (feedType.Contains("GitHub", StringComparison.OrdinalIgnoreCase))
        {
            headers["Accept"] = "application/vnd.github+json";
            headers["X-GitHub-Api-Version"] = "2022-11-28";
        }

        if (!string.IsNullOrWhiteSpace(feed.Username) && !string.IsNullOrWhiteSpace(feed.Password))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{feed.Username}:{feed.Password}"));
            headers["Authorization"] = $"Basic {credentials}";
        }
        else if (!string.IsNullOrWhiteSpace(feed.Password))
        {
            headers["Authorization"] = feedType.Contains("GitHub", StringComparison.OrdinalIgnoreCase)
                ? $"token {feed.Password}"
                : $"Bearer {feed.Password}";
        }

        return headers;
    }

    internal static Dictionary<string, byte[]> ExtractArchive(byte[] archiveBytes)
    {
        if (IsTarGz(archiveBytes))
            return ExtractTarGz(archiveBytes);

        if (IsZip(archiveBytes))
            return ExtractZip(archiveBytes);

        throw new InvalidOperationException("Unsupported archive format. Expected .tar.gz or .zip.");
    }

    private static bool IsTarGz(byte[] bytes) => bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;

    private static bool IsZip(byte[] bytes) => bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04;

    private static Dictionary<string, byte[]> ExtractTarGz(byte[] archiveBytes)
    {
        var files = new Dictionary<string, byte[]>();

        using var memoryStream = new MemoryStream(archiveBytes);
        using var gzipStream = new GZipStream(memoryStream, CompressionMode.Decompress);
        using var tarReader = new TarReader(gzipStream);

        while (tarReader.GetNextEntry(copyData: true) is { } entry)
        {
            if (entry.EntryType != TarEntryType.RegularFile) continue;
            if (!IsIncludedFile(entry.Name)) continue;

            var relativePath = NormalizeEntryPath(entry.Name);
            if (string.IsNullOrEmpty(relativePath)) continue;

            using var entryStream = entry.DataStream;

            if (entryStream == null) continue;

            using var ms = new MemoryStream();
            entryStream.CopyTo(ms);
            files[relativePath] = ms.ToArray();
        }

        return files;
    }

    private static Dictionary<string, byte[]> ExtractZip(byte[] archiveBytes)
    {
        var files = new Dictionary<string, byte[]>();

        using var memoryStream = new MemoryStream(archiveBytes);
        using var archive = new ZipArchive(memoryStream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (!IsIncludedFile(entry.FullName)) continue;

            var relativePath = NormalizeEntryPath(entry.FullName);
            if (string.IsNullOrEmpty(relativePath)) continue;

            using var entryStream = entry.Open();
            using var ms = new MemoryStream();
            entryStream.CopyTo(ms);
            files[relativePath] = ms.ToArray();
        }

        return files;
    }

    private static bool IsIncludedFile(string path)
    {
        var ext = Path.GetExtension(path);
        return YamlExtensions.Contains(ext);
    }

    private static string NormalizeEntryPath(string entryPath)
    {
        var normalized = entryPath.Replace('\\', '/').TrimStart('/');

        // Strip leading directory (common in archives: chartname/templates/file.yaml)
        var firstSlash = normalized.IndexOf('/');
        if (firstSlash > 0 && firstSlash < normalized.Length - 1)
            normalized = normalized[(firstSlash + 1)..];

        return normalized;
    }
}
