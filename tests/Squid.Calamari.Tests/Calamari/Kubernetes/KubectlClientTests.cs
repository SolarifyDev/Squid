using Squid.Calamari.Execution.Output;
using Squid.Calamari.Execution.Processes;
using Squid.Calamari.Kubernetes;

namespace Squid.Calamari.Tests.Calamari.Kubernetes;

public class KubectlClientTests
{
    [Fact]
    public async Task ApplyAsync_BuildsKubectlInvocation_AndCollectsServiceMessages()
    {
        ProcessInvocation capturedInvocation = null;
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.ExecuteAsync(
                It.IsAny<ProcessInvocation>(),
                It.IsAny<IProcessOutputSink>(),
                It.IsAny<CancellationToken>()))
            .Callback<ProcessInvocation, IProcessOutputSink, CancellationToken>((invocation, sink, _) =>
            {
                capturedInvocation = invocation;
                sink.WriteStdout("applying...");
                sink.WriteStdout("##squid[setVariable name='Applied' value='True']");
            })
            .ReturnsAsync(new ProcessResult(0));

        var client = new KubectlClient(runner.Object);

        var result = await client.ApplyAsync(
            new KubectlApplyRequest
            {
                WorkingDirectory = "/tmp",
                ManifestFilePath = "/tmp/manifest.yaml",
                Namespace = "demo"
            },
            CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        result.OutputVariables.Count.ShouldBe(1);
        result.OutputVariables[0].Name.ShouldBe("Applied");
        result.OutputVariables[0].Value.ShouldBe("True");

        capturedInvocation.ShouldNotBeNull();
        capturedInvocation.Executable.ShouldBe("kubectl");
        capturedInvocation.WorkingDirectory.ShouldBe("/tmp");
        capturedInvocation.Arguments.ShouldBe(["apply", "-f", "/tmp/manifest.yaml", "--namespace", "demo"]);
    }
}
