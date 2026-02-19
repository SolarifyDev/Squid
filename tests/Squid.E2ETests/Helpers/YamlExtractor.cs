using System.IO.Compression;
using Squid.E2ETests.Infrastructure;

namespace Squid.E2ETests.Helpers;

public static class YamlExtractor
{
    public static Dictionary<string, byte[]> Extract(CapturingExecutionStrategy capture)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var request in capture.CapturedRequests)
        {
            foreach (var file in request.Files)
            {
                if (!file.Key.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) &&
                    !file.Key.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                    continue;

                result[file.Key] = file.Value;
            }
        }

        if (result.Count == 0)
            throw new InvalidOperationException("No YAML files found in captured execution requests");

        return result;
    }

    public static Dictionary<string, byte[]> Extract(CapturingHalibutClientFactory capture)
    {
        var packageFile = FindYamlPackageFile(capture.CapturedFileBytes);

        if (packageFile.Value == null)
            throw new InvalidOperationException("No YAML NuGet package found in captured files");

        return ExtractYamlFromPackage(packageFile.Value);
    }

    private static KeyValuePair<string, byte[]> FindYamlPackageFile(Dictionary<string, byte[]> capturedFiles)
    {
        return capturedFiles.FirstOrDefault(kv =>
            kv.Key.StartsWith("squid.", StringComparison.OrdinalIgnoreCase) &&
            kv.Key.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, byte[]> ExtractYamlFromPackage(byte[] packageBytes)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        using var stream = new MemoryStream(packageBytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) &&
                !entry.FullName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                continue;

            using var entryStream = entry.Open();
            using var ms = new MemoryStream();
            entryStream.CopyTo(ms);
            result[entry.Name] = ms.ToArray();
        }

        return result;
    }
}
