using Squid.Calamari.Variables;

namespace Squid.Calamari.Kubernetes;

public sealed class TokenSubstitutingYamlManifestRenderer : IKubernetesManifestRenderer
{
    public Task<string> RenderToFileAsync(KubernetesApplyRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var yaml = File.ReadAllText(request.YamlFilePath);
        yaml = ReplaceTokens(yaml, request.Variables);

        var outputPath = Path.Combine(
            request.WorkingDirectory,
            $".squid-expanded-{Guid.NewGuid():N}-{Path.GetFileName(request.YamlFilePath)}");

        File.WriteAllText(outputPath, yaml);
        request.TemporaryFiles?.Add(outputPath);

        return Task.FromResult(outputPath);
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
}
