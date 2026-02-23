using System.IO.Compression;

namespace Squid.Calamari.Compatibility.Octopus;

/// <summary>
/// Resolves a compat `--package` argument to a concrete raw YAML file.
/// Supports direct .yaml/.yml files and single-manifest .zip/.nupkg archives.
/// </summary>
public sealed class OctopusRawYamlPackageResolver : IOctopusRawYamlPackageResolver
{
    public Task<ResolvedRawYamlPackage> ResolveAsync(string packagePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(packagePath))
            throw new ArgumentException("Package path is required.", nameof(packagePath));

        var fullPath = Path.GetFullPath(packagePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Package path was not found.", fullPath);

        var extension = Path.GetExtension(fullPath);
        if (IsYaml(extension))
        {
            return Task.FromResult(new ResolvedRawYamlPackage
            {
                YamlFilePath = fullPath
            });
        }

        if (IsArchive(extension))
            return ResolveArchiveAsync(fullPath, ct);

        throw new InvalidOperationException(
            $"Unsupported package extension '{extension}'. Expected .yaml/.yml, .zip, or .nupkg.");
    }

    private static Task<ResolvedRawYamlPackage> ResolveArchiveAsync(string archivePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var extractionDir = Path.Combine(
            Path.GetTempPath(),
            "squid-calamari-octopus-compat-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(extractionDir);

        try
        {
            ZipFile.ExtractToDirectory(archivePath, extractionDir);

            var yamlFiles = Directory
                .EnumerateFiles(extractionDir, "*.*", SearchOption.AllDirectories)
                .Where(static p => IsYaml(Path.GetExtension(p)))
                .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (yamlFiles.Length == 0)
                throw new InvalidOperationException("Package archive did not contain any .yaml/.yml files.");

            if (yamlFiles.Length > 1)
            {
                throw new InvalidOperationException(
                    "Package archive contains multiple .yaml/.yml files; raw-yaml compat currently requires exactly one manifest file.");
            }

            return Task.FromResult(new ResolvedRawYamlPackage
            {
                YamlFilePath = yamlFiles[0],
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

    private static bool IsArchive(string extension)
        => extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".nupkg", StringComparison.OrdinalIgnoreCase);

    private static bool IsYaml(string extension)
        => extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase);
}
