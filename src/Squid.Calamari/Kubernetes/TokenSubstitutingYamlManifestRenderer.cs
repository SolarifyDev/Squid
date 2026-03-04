using Squid.Calamari.Variables;

namespace Squid.Calamari.Kubernetes;

public sealed class TokenSubstitutingYamlManifestRenderer : IKubernetesManifestRenderer
{
    public Task<RenderedKubernetesManifest> RenderAsync(
        KubernetesApplyRequest request,
        ResolvedKubernetesManifestSource source,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return source.IsSingleFile
            ? Task.FromResult(RenderSingleFile(request, source.ManifestFilePaths[0]))
            : Task.FromResult(RenderManifestSet(request, source));
    }

    internal static string ReplaceTokens(string yaml, IEnumerable<KeyValuePair<string, string>> variables)
    {
        foreach (var (name, value) in variables)
        {
            if (string.IsNullOrEmpty(name))
                continue;

            yaml = yaml.Replace($"#{{{name}}}", value ?? string.Empty, StringComparison.Ordinal);
        }

        return yaml;
    }

    private RenderedKubernetesManifest RenderSingleFile(KubernetesApplyRequest request, string inputFilePath)
    {
        var yaml = File.ReadAllText(inputFilePath);
        yaml = ReplaceTokens(yaml, request.Variables);

        var outputPath = Path.Combine(
            request.WorkingDirectory,
            $".squid-expanded-{Guid.NewGuid():N}-{Path.GetFileName(inputFilePath)}");

        File.WriteAllText(outputPath, yaml);
        request.TemporaryFiles?.Add(outputPath);

        return new RenderedKubernetesManifest
        {
            ApplyPath = outputPath,
            Recursive = false
        };
    }

    private RenderedKubernetesManifest RenderManifestSet(
        KubernetesApplyRequest request,
        ResolvedKubernetesManifestSource source)
    {
        var outputDir = Path.Combine(
            request.WorkingDirectory,
            $".squid-expanded-manifests-{Guid.NewGuid():N}");

        Directory.CreateDirectory(outputDir);

        foreach (var inputFilePath in source.ManifestFilePaths)
        {
            var relativePath = Path.GetRelativePath(source.ManifestRootDirectory, inputFilePath);
            if (relativePath.StartsWith("..", StringComparison.Ordinal))
                throw new InvalidOperationException("Manifest file is outside the resolved manifest root.");

            var yaml = File.ReadAllText(inputFilePath);
            yaml = ReplaceTokens(yaml, request.Variables);

            var outputFilePath = Path.Combine(outputDir, relativePath);
            var outputSubDir = Path.GetDirectoryName(outputFilePath);
            if (!string.IsNullOrWhiteSpace(outputSubDir))
                Directory.CreateDirectory(outputSubDir);

            File.WriteAllText(outputFilePath, yaml);
        }

        request.TemporaryFiles?.Add(outputDir);

        return new RenderedKubernetesManifest
        {
            ApplyPath = outputDir,
            Recursive = true
        };
    }
}
