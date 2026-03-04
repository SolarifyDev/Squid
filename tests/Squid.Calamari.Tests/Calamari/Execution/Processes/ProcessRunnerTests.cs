using Squid.Calamari.Execution.Output;
using Squid.Calamari.Execution.Processes;

namespace Squid.Calamari.Tests.Calamari.Execution.Processes;

public class ProcessRunnerTests
{
    [Fact]
    public async Task ExecuteAsync_CapturesStdoutStderrAndExitCode()
    {
        var runner = new ProcessRunner();
        var sink = new RecordingSink();
        var invocation = new ProcessInvocation(
            executable: "bash",
            arguments: ["-c", "printf 'out\\n'; printf 'err\\n' 1>&2; exit 7"],
            workingDirectory: Directory.GetCurrentDirectory());

        var result = await runner.ExecuteAsync(invocation, sink, CancellationToken.None);

        result.ExitCode.ShouldBe(7);
        sink.Stdout.ShouldContain("out");
        sink.Stderr.ShouldContain("err");
    }

    [Fact]
    public async Task ExecuteAsync_AppliesEnvironmentVariables()
    {
        var runner = new ProcessRunner();
        var sink = new RecordingSink();
        var invocation = new ProcessInvocation(
            executable: "bash",
            arguments: ["-c", "printf '%s\\n' \"$SQUID_TEST_ENV\""],
            workingDirectory: Directory.GetCurrentDirectory(),
            environmentVariables: new Dictionary<string, string> { ["SQUID_TEST_ENV"] = "works" });

        var result = await runner.ExecuteAsync(invocation, sink, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        sink.Stdout.ShouldContain("works");
    }

    private sealed class RecordingSink : IProcessOutputSink
    {
        public List<string> Stdout { get; } = new();
        public List<string> Stderr { get; } = new();

        public void WriteStdout(string line) => Stdout.Add(line);

        public void WriteStderr(string line) => Stderr.Add(line);
    }
}
