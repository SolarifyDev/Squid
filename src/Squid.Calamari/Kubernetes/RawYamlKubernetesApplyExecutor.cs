using Squid.Calamari.Execution;

namespace Squid.Calamari.Kubernetes;

public sealed class RawYamlKubernetesApplyExecutor : IKubernetesApplyExecutor
{
    private readonly IKubernetesManifestSourceResolver _manifestSourceResolver;
    private readonly IKubernetesManifestRenderer _manifestRenderer;
    private readonly IKubectlClient _kubectlClient;

    public RawYamlKubernetesApplyExecutor()
        : this(
            new KubernetesManifestSourceResolver(),
            new TokenSubstitutingYamlManifestRenderer(),
            new KubectlClient())
    {
    }

    public RawYamlKubernetesApplyExecutor(
        IKubernetesManifestRenderer manifestRenderer,
        IKubectlClient kubectlClient)
        : this(new KubernetesManifestSourceResolver(), manifestRenderer, kubectlClient)
    {
    }

    public RawYamlKubernetesApplyExecutor(
        IKubernetesManifestSourceResolver manifestSourceResolver,
        IKubernetesManifestRenderer manifestRenderer,
        IKubectlClient kubectlClient)
    {
        _manifestSourceResolver = manifestSourceResolver;
        _manifestRenderer = manifestRenderer;
        _kubectlClient = kubectlClient;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(KubernetesApplyRequest request, CancellationToken ct)
    {
        var resolvedManifestSource = await _manifestSourceResolver.ResolveAsync(request.YamlFilePath, ct)
            .ConfigureAwait(false);

        if (request.TemporaryFiles is not null)
        {
            foreach (var path in resolvedManifestSource.CleanupPaths)
                request.TemporaryFiles.Add(path);
        }

        var resolvedRequest = new KubernetesApplyRequest
        {
            WorkingDirectory = request.WorkingDirectory,
            YamlFilePath = resolvedManifestSource.ManifestFilePath,
            Variables = request.Variables,
            Namespace = request.Namespace,
            TemporaryFiles = request.TemporaryFiles
        };

        var renderedManifestPath = await _manifestRenderer.RenderToFileAsync(resolvedRequest, ct)
            .ConfigureAwait(false);

        return await _kubectlClient.ApplyAsync(
                new KubectlApplyRequest
                {
                    WorkingDirectory = request.WorkingDirectory,
                    ManifestFilePath = renderedManifestPath,
                    Namespace = request.Namespace
                },
                ct)
            .ConfigureAwait(false);
    }
}
