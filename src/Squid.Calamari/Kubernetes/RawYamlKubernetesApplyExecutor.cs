using Squid.Calamari.Execution;

namespace Squid.Calamari.Kubernetes;

public sealed class RawYamlKubernetesApplyExecutor : IKubernetesApplyExecutor
{
    private readonly IKubernetesManifestRenderer _manifestRenderer;
    private readonly IKubectlClient _kubectlClient;

    public RawYamlKubernetesApplyExecutor()
        : this(new TokenSubstitutingYamlManifestRenderer(), new KubectlClient())
    {
    }

    public RawYamlKubernetesApplyExecutor(
        IKubernetesManifestRenderer manifestRenderer,
        IKubectlClient kubectlClient)
    {
        _manifestRenderer = manifestRenderer;
        _kubectlClient = kubectlClient;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(KubernetesApplyRequest request, CancellationToken ct)
    {
        var renderedManifestPath = await _manifestRenderer.RenderToFileAsync(request, ct)
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
