using Squid.Calamari.Execution;
using Squid.Calamari.Kubernetes;
using Squid.Calamari.Variables;

namespace Squid.Calamari.Tests.Calamari.Kubernetes;

public class RawYamlKubernetesApplyExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_RendersManifest_ThenCallsKubectl()
    {
        var renderer = new Mock<IKubernetesManifestRenderer>();
        var client = new Mock<IKubectlClient>();
        var executor = new RawYamlKubernetesApplyExecutor(renderer.Object, client.Object);
        var request = new KubernetesApplyRequest
        {
            WorkingDirectory = "/tmp/work",
            YamlFilePath = "/tmp/work/input.yaml",
            Variables = new VariableSet(),
            Namespace = "ns-a"
        };

        renderer.Setup(r => r.RenderToFileAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync("/tmp/work/.expanded-input.yaml");

        client.Setup(c => c.ApplyAsync(
                It.IsAny<KubectlApplyRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandExecutionResult(0));

        var result = await executor.ExecuteAsync(request, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        renderer.Verify(r => r.RenderToFileAsync(request, It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.ApplyAsync(
            It.Is<KubectlApplyRequest>(k =>
                k.WorkingDirectory == "/tmp/work" &&
                k.ManifestFilePath == "/tmp/work/.expanded-input.yaml" &&
                k.Namespace == "ns-a"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
