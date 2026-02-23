using Squid.Calamari.Execution;
using Squid.Calamari.Kubernetes;
using Squid.Calamari.Variables;

namespace Squid.Calamari.Tests.Calamari.Kubernetes;

public class RawYamlKubernetesApplyExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_RendersManifest_ThenCallsKubectl()
    {
        var resolver = new Mock<IKubernetesManifestSourceResolver>();
        var renderer = new Mock<IKubernetesManifestRenderer>();
        var client = new Mock<IKubectlClient>();
        var executor = new RawYamlKubernetesApplyExecutor(resolver.Object, renderer.Object, client.Object);
        var request = new KubernetesApplyRequest
        {
            WorkingDirectory = "/tmp/work",
            YamlFilePath = "/tmp/work/input.yaml",
            Variables = new VariableSet(),
            Namespace = "ns-a"
        };

        resolver.Setup(r => r.ResolveAsync("/tmp/work/input.yaml", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedKubernetesManifestSource
            {
                ManifestFilePath = "/tmp/work/resolved.yaml",
                CleanupPaths = ["/tmp/work/extracted"]
            });

        renderer.Setup(r => r.RenderToFileAsync(
                It.Is<KubernetesApplyRequest>(k => k.YamlFilePath == "/tmp/work/resolved.yaml"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("/tmp/work/.expanded-input.yaml");

        client.Setup(c => c.ApplyAsync(
                It.IsAny<KubectlApplyRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandExecutionResult(0));

        var result = await executor.ExecuteAsync(request, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        resolver.Verify(r => r.ResolveAsync("/tmp/work/input.yaml", It.IsAny<CancellationToken>()), Times.Once);
        renderer.Verify(r => r.RenderToFileAsync(
            It.Is<KubernetesApplyRequest>(k => k.YamlFilePath == "/tmp/work/resolved.yaml"),
            It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.ApplyAsync(
            It.Is<KubectlApplyRequest>(k =>
                k.WorkingDirectory == "/tmp/work" &&
                k.ManifestFilePath == "/tmp/work/.expanded-input.yaml" &&
                k.Namespace == "ns-a"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_TracksResolverCleanupPaths_WhenTemporaryFilesCollectionProvided()
    {
        var resolver = new Mock<IKubernetesManifestSourceResolver>();
        var renderer = new Mock<IKubernetesManifestRenderer>();
        var client = new Mock<IKubectlClient>();
        var executor = new RawYamlKubernetesApplyExecutor(resolver.Object, renderer.Object, client.Object);
        var tempFiles = new List<string>();
        var request = new KubernetesApplyRequest
        {
            WorkingDirectory = "/tmp/work",
            YamlFilePath = "/tmp/work/input.nupkg",
            Variables = new VariableSet(),
            TemporaryFiles = tempFiles
        };

        resolver.Setup(r => r.ResolveAsync("/tmp/work/input.nupkg", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedKubernetesManifestSource
            {
                ManifestFilePath = "/tmp/work/extracted/app.yaml",
                CleanupPaths = ["/tmp/work/extracted"]
            });

        renderer.Setup(r => r.RenderToFileAsync(It.IsAny<KubernetesApplyRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/tmp/work/rendered.yaml");
        client.Setup(c => c.ApplyAsync(It.IsAny<KubectlApplyRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandExecutionResult(0));

        await executor.ExecuteAsync(request, CancellationToken.None);

        tempFiles.ShouldContain("/tmp/work/extracted");
    }
}
