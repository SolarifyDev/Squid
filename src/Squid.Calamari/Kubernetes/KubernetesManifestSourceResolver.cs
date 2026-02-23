using System.IO.Compression;

namespace Squid.Calamari.Kubernetes;

/// <summary>
/// Resolves a raw YAML manifest source into exactly one concrete file path.
/// Supports direct file paths, directory paths, and simple glob patterns.
/// </summary>
public sealed class KubernetesManifestSourceResolver : IKubernetesManifestSourceResolver
{
    public Task<ResolvedKubernetesManifestSource> ResolveAsync(string manifestSourcePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(manifestSourcePath))
            throw new ArgumentException("Manifest source path is required.", nameof(manifestSourcePath));

        var fullPath = Path.GetFullPath(manifestSourcePath);

        if (File.Exists(fullPath))
            return ResolveFromFileAsync(fullPath, ct);

        if (Directory.Exists(fullPath))
        {
            return Task.FromResult(new ResolvedKubernetesManifestSource
            {
                ManifestFilePath = ResolveFromDirectory(fullPath)
            });
        }

        if (LooksLikeGlob(manifestSourcePath))
        {
            return Task.FromResult(new ResolvedKubernetesManifestSource
            {
                ManifestFilePath = ResolveFromGlob(manifestSourcePath)
            });
        }

        throw new FileNotFoundException("Manifest source path was not found.", fullPath);
    }

    private static Task<ResolvedKubernetesManifestSource> ResolveFromFileAsync(string fullPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var extension = Path.GetExtension(fullPath);
        if (IsYamlExtension(extension))
        {
            return Task.FromResult(new ResolvedKubernetesManifestSource
            {
                ManifestFilePath = fullPath
            });
        }

        if (IsArchiveExtension(extension))
            return ResolveFromArchiveAsync(fullPath, ct);

        throw new InvalidOperationException(
            $"Unsupported manifest source file '{fullPath}'. Expected .yaml/.yml, .zip, or .nupkg.");
    }

    private static Task<ResolvedKubernetesManifestSource> ResolveFromArchiveAsync(string archivePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var extractionDir = Path.Combine(
            Path.GetTempPath(),
            "squid-calamari-k8s-manifest-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(extractionDir);

        try
        {
            ZipFile.ExtractToDirectory(archivePath, extractionDir);

            var candidates = Directory
                .EnumerateFiles(extractionDir, "*.*", SearchOption.AllDirectories)
                .Where(static p => IsYamlExtension(Path.GetExtension(p)))
                .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var manifestFilePath = EnsureSingleYaml(candidates, $"Archive '{archivePath}'");

            return Task.FromResult(new ResolvedKubernetesManifestSource
            {
                ManifestFilePath = manifestFilePath,
                CleanupPaths = [extractionDir]
            });
        }
        catch
        {
            if (Directory.Exists(extractionDir))
                Directory.Delete(extractionDir, recursive: true);
            throw;
        }
    }

    private static string ResolveFromDirectory(string directoryPath)
    {
        var candidates = Directory
            .EnumerateFiles(directoryPath, "*.*", SearchOption.TopDirectoryOnly)
            .Where(static p => IsYamlExtension(Path.GetExtension(p)))
            .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return EnsureSingleYaml(candidates, $"Directory '{directoryPath}'");
    }

    private static string ResolveFromGlob(string globPath)
    {
        var normalized = Path.GetFullPath(globPath);
        var directoryPath = Path.GetDirectoryName(normalized);
        var filePattern = Path.GetFileName(normalized);

        if (string.IsNullOrWhiteSpace(directoryPath) || string.IsNullOrWhiteSpace(filePattern))
            throw new InvalidOperationException($"Invalid manifest glob path '{globPath}'.");
        if (!Directory.Exists(directoryPath))
            throw new DirectoryNotFoundException($"Manifest glob directory '{directoryPath}' was not found.");

        var candidates = Directory
            .EnumerateFiles(directoryPath, filePattern, SearchOption.TopDirectoryOnly)
            .Where(static p => IsYamlExtension(Path.GetExtension(p)))
            .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return EnsureSingleYaml(candidates, $"Glob '{globPath}'");
    }

    private static string EnsureSingleYaml(string[] candidates, string sourceDescription)
    {
        if (candidates.Length == 0)
            throw new InvalidOperationException($"{sourceDescription} did not resolve to any .yaml/.yml files.");
        if (candidates.Length > 1)
            throw new InvalidOperationException($"{sourceDescription} resolved to multiple .yaml/.yml files; expected exactly one.");

        return candidates[0];
    }

    private static bool LooksLikeGlob(string path)
        => path.IndexOfAny(['*', '?']) >= 0;

    private static bool IsYamlExtension(string extension)
        => extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase);

    private static bool IsArchiveExtension(string extension)
        => extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".nupkg", StringComparison.OrdinalIgnoreCase);
}
