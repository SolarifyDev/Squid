using System;
using System.IO;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Kubernetes;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class LocalProcessRunnerTests
{
    private readonly LocalProcessRunner _runner = new();

    // === Success ===

    [Fact]
    public async Task RunAsync_EchoCommand_ReturnsSuccess()
    {
        var result = await _runner.RunAsync("echo", "hello", Path.GetTempPath(), CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task RunAsync_EchoCommand_CapturesOutput()
    {
        var result = await _runner.RunAsync("echo", "hello-from-test", Path.GetTempPath(), CancellationToken.None);

        result.LogLines.ShouldContain(line => line.Contains("hello-from-test"));
    }

    // === Failure ===

    [Fact]
    public async Task RunAsync_NonZeroExitCode_ReturnsFailure()
    {
        var result = await _runner.RunAsync("bash", "-c \"exit 5\"", Path.GetTempPath(), CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.ExitCode.ShouldBe(5);
    }

    // === Cancellation ===

    [Fact]
    public async Task RunAsync_CancellationRequested_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(
            () => _runner.RunAsync("bash", "-c \"sleep 60\"", Path.GetTempPath(), cts.Token));
    }

    // === Concurrent Output ===

    [Fact]
    public async Task RunAsync_ConcurrentStdoutStderr_CollectsAllLines()
    {
        var script = "for i in $(seq 1 50); do echo \"out-$i\"; echo \"err-$i\" >&2; done";

        var result = await _runner.RunAsync("bash", $"-c \"{script}\"", Path.GetTempPath(), CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.LogLines.Count.ShouldBeGreaterThanOrEqualTo(100);
    }
}
