using Squid.Calamari.Execution;
using Squid.Calamari.Execution.Output;
using Squid.Calamari.Execution.Processes;

namespace Squid.Calamari.Tests.Calamari.Execution;

public class BashScriptExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_UsesProcessRunnerWithBashInvocation()
    {
        ProcessInvocation capturedInvocation = null;
        IProcessOutputSink capturedSink = null;

        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.ExecuteAsync(
                It.IsAny<ProcessInvocation>(),
                It.IsAny<IProcessOutputSink>(),
                It.IsAny<CancellationToken>()))
            .Callback<ProcessInvocation, IProcessOutputSink, CancellationToken>((invocation, sink, _) =>
            {
                capturedInvocation = invocation;
                capturedSink = sink;
            })
            .ReturnsAsync(new ProcessResult(13));

        var executor = new BashScriptExecutor(runner.Object);
        var processor = new ScriptOutputProcessor();

        var exitCode = await executor.ExecuteAsync("/tmp/script.sh", "/tmp", processor, CancellationToken.None);

        exitCode.ShouldBe(13);
        capturedInvocation.ShouldNotBeNull();
        capturedInvocation.Executable.ShouldBe("bash");
        capturedInvocation.Arguments.ShouldBe(["/tmp/script.sh"]);
        capturedInvocation.WorkingDirectory.ShouldBe("/tmp");
        capturedSink.ShouldNotBeNull();
    }
}
