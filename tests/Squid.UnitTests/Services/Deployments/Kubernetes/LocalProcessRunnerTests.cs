using System;
using System.IO;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Constants;

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

    // === Timeout ===

    [Fact]
    public async Task RunAsync_TimeoutExceeded_ReturnsTimeoutExitCode()
    {
        var result = await _runner.RunAsync("bash", "-c \"sleep 60\"", Path.GetTempPath(), CancellationToken.None, timeout: TimeSpan.FromMilliseconds(500));

        result.Success.ShouldBeFalse();
        result.ExitCode.ShouldBe(ScriptExitCodes.Timeout);
    }

    [Fact]
    public async Task RunAsync_TimeoutNotExceeded_ReturnsNormally()
    {
        var result = await _runner.RunAsync("echo", "fast", Path.GetTempPath(), CancellationToken.None, timeout: TimeSpan.FromSeconds(10));

        result.Success.ShouldBeTrue();
        result.ExitCode.ShouldBe(0);
    }

    // === Graceful Termination ===

    [Fact]
    public async Task RunAsync_CancellationRequested_KillsProcessGracefully()
    {
        // Process that traps SIGTERM and writes marker before exiting
        var script = "trap 'echo SIGTERM_RECEIVED; exit 0' TERM; sleep 60 & wait";
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Should.ThrowAsync<OperationCanceledException>(
            () => _runner.RunAsync("bash", $"-c \"{script}\"", Path.GetTempPath(), cts.Token));

        // Test passes if the process was killed without hanging — SIGTERM sent first, SIGKILL after 5s grace
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

    // === Output Drain Sync (Fix 14) ===

    [Fact]
    public async Task RunAsync_LargeOutput_AllLinesCaptured()
    {
        var script = "for i in $(seq 1 1000); do echo \"line-$i\"; done";

        var result = await _runner.RunAsync("bash", $"-c \"{script}\"", Path.GetTempPath(), CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.LogLines.Count.ShouldBe(1000);
        result.LogLines.ShouldContain(l => l.Contains("line-1000"));
    }

    [Fact]
    public async Task RunAsync_StderrFlush_AllLinesCaptured()
    {
        var script = "for i in $(seq 1 100); do echo \"err-$i\" >&2; done";

        var result = await _runner.RunAsync("bash", $"-c \"{script}\"", Path.GetTempPath(), CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        result.StderrLines.Count.ShouldBe(100);
        result.StderrLines.ShouldContain(l => l.Contains("err-100"));
    }
}
