using System.Text;
using System.Text.Json;
using System.IO.Compression;
using Halibut;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Message.Contracts.Tentacle;

namespace Squid.Core.Services.DeploymentExecution.Tentacle;

internal static class PackageReferenceScriptFileBridge
{
    internal const string ManifestFileName = "package-references.json";
    internal const string PackageReferencesDirectory = "package-references";

    internal static IReadOnlyList<ScriptFile> BuildScriptFiles(IReadOnlyList<PackageAcquisitionResult>? packageReferences)
    {
        if (packageReferences == null || packageReferences.Count == 0)
            return Array.Empty<ScriptFile>();

        var files = new List<ScriptFile>();
        var manifest = new List<PackageReferenceManifestEntry>();
        var usedPackageRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var packageReference in packageReferences)
        {
            if (packageReference == null) continue;

            if (string.IsNullOrWhiteSpace(packageReference.LocalPath))
                throw new InvalidOperationException($"Package '{packageReference.PackageId}' has no acquired local path.");

            if (!File.Exists(packageReference.LocalPath))
                throw new FileNotFoundException($"Acquired package '{packageReference.PackageId}' was not found at '{packageReference.LocalPath}'.", packageReference.LocalPath);

            var packageRootPath = BuildPackageRelativePath(packageReference, usedPackageRoots);
            var packageFiles = BuildExtractedPackageFiles(packageReference.LocalPath, packageRootPath, usedFilePaths);

            files.AddRange(packageFiles);
            manifest.Add(new PackageReferenceManifestEntry(
                packageReference.PackageId,
                packageReference.Version,
                packageRootPath,
                packageReference.SizeBytes,
                packageReference.Hash));
        }

        if (manifest.Count == 0)
            return Array.Empty<ScriptFile>();

        var manifestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest));
        files.Insert(0, new ScriptFile(ManifestFileName, DataStream.FromBytes(manifestBytes), null));

        return files;
    }

    internal static string BuildPackageRelativePath(PackageAcquisitionResult packageReference, ISet<string>? usedPaths = null)
    {
        ArgumentNullException.ThrowIfNull(packageReference);

        var packageId = SafePathSegment(packageReference.PackageId, "package");
        var version = SafePathSegment(packageReference.Version, "version");
        var baseName = $"{packageId}.{version}";
        var relativePath = Path.Combine(PackageReferencesDirectory, baseName).Replace('\\', '/');

        if (usedPaths == null)
            return relativePath;

        if (usedPaths.Add(relativePath))
            return relativePath;

        var suffix = 2;
        while (true)
        {
            var candidate = Path.Combine(PackageReferencesDirectory, $"{packageId}.{version}.{suffix}").Replace('\\', '/');
            if (usedPaths.Add(candidate))
                return candidate;

            suffix++;
        }
    }

    private static List<ScriptFile> BuildExtractedPackageFiles(string localPath, string packageRootPath, ISet<string> usedFilePaths)
    {
        try
        {
            using var archive = ZipFile.OpenRead(localPath);
            var files = new List<ScriptFile>();

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                var entryPath = NormalizeArchiveEntryPath(entry.FullName);
                if (string.IsNullOrEmpty(entryPath))
                    continue;

                var relativePath = $"{packageRootPath}/{entryPath}";
                if (!usedFilePaths.Add(relativePath))
                    throw new InvalidOperationException($"Package archive '{localPath}' contains duplicate entry '{entry.FullName}'.");

                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                files.Add(new ScriptFile(relativePath, DataStream.FromBytes(buffer.ToArray()), null));
            }

            if (files.Count == 0)
                throw new InvalidOperationException($"Package archive '{localPath}' does not contain any files.");

            return files;
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException($"Package archive '{localPath}' could not be read as a zip/nupkg archive.", ex);
        }
    }

    private static string NormalizeArchiveEntryPath(string entryPath)
    {
        var candidate = entryPath.Replace('\\', '/');
        if (candidate.StartsWith("/", StringComparison.Ordinal) || candidate.Contains(":", StringComparison.Ordinal))
            throw new InvalidOperationException($"Package archive contains unsafe entry path '{entryPath}'.");

        var normalized = candidate.Trim('/');
        if (string.IsNullOrEmpty(normalized))
            return string.Empty;

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment =>
                segment == "." ||
                segment == ".." ||
                Path.IsPathRooted(segment)))
        {
            throw new InvalidOperationException($"Package archive contains unsafe entry path '{entryPath}'.");
        }

        return string.Join('/', segments);
    }

    private static string SafePathSegment(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var chars = value
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_')
            .ToArray();
        var sanitized = new string(chars).Trim('.', '_', '-');

        return string.IsNullOrEmpty(sanitized) ? fallback : sanitized;
    }

    private sealed record PackageReferenceManifestEntry(
        string PackageId,
        string Version,
        string PackagePath,
        long SizeBytes,
        string Hash);
}
