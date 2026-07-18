using System.Text;
using System.Text.Json;
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
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var packageReference in packageReferences)
        {
            if (packageReference == null) continue;

            if (string.IsNullOrWhiteSpace(packageReference.LocalPath))
                throw new InvalidOperationException($"Package '{packageReference.PackageId}' has no acquired local path.");

            if (!File.Exists(packageReference.LocalPath))
                throw new FileNotFoundException($"Acquired package '{packageReference.PackageId}' was not found at '{packageReference.LocalPath}'.", packageReference.LocalPath);

            var packagePath = BuildPackageRelativePath(packageReference, usedPaths);
            var packageBytes = File.ReadAllBytes(packageReference.LocalPath);

            files.Add(new ScriptFile(packagePath, DataStream.FromBytes(packageBytes), null));
            manifest.Add(new PackageReferenceManifestEntry(
                packageReference.PackageId,
                packageReference.Version,
                packagePath,
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
        var extension = SafeExtension(packageReference.LocalPath);
        var baseName = $"{packageId}.{version}{extension}";
        var relativePath = Path.Combine(PackageReferencesDirectory, baseName).Replace('\\', '/');

        if (usedPaths == null)
            return relativePath;

        if (usedPaths.Add(relativePath))
            return relativePath;

        var suffix = 2;
        while (true)
        {
            var candidate = Path.Combine(PackageReferencesDirectory, $"{packageId}.{version}.{suffix}{extension}").Replace('\\', '/');
            if (usedPaths.Add(candidate))
                return candidate;

            suffix++;
        }
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

    private static string SafeExtension(string localPath)
    {
        var extension = Path.GetExtension(localPath);

        if (string.IsNullOrWhiteSpace(extension))
            return ".package";

        var chars = extension
            .Select(c => char.IsAsciiLetterOrDigit(c) || c == '.' ? c : '_')
            .ToArray();
        var sanitized = new string(chars);

        return sanitized == "." ? ".package" : sanitized;
    }

    private sealed record PackageReferenceManifestEntry(
        string PackageId,
        string Version,
        string PackagePath,
        long SizeBytes,
        string Hash);
}
