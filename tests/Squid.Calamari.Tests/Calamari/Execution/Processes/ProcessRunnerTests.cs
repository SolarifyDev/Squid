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

    [Fact]
    public void ScriptTimeoutMinutesEnvVar_ConstantNamePinned()
    {
        // Renaming this breaks every operator who pinned a Calamari script timeout via env.
        ProcessRunner.ScriptTimeoutMinutesEnvVar.ShouldBe("SQUID_CALAMARI_SCRIPT_TIMEOUT_MINUTES");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("0")]
    [InlineData("-5")]
    public void ParseTimeoutMinutes_UnsetInvalidOrNonPositive_ReturnsNull(string raw)
    {
        // Fail-open to "no timeout" — preserves the historical infinite-wait behaviour.
        ProcessRunner.ParseTimeoutMinutes(raw).ShouldBeNull();
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("  10  ", 10)]
    [InlineData("120", 120)]
    public void ParseTimeoutMinutes_PositiveInteger_ReturnsMinutes(string raw, int expectedMinutes)
    {
        ProcessRunner.ParseTimeoutMinutes(raw).ShouldBe(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Fact]
    public async Task ExecuteAsync_ProcessExceedsTimeout_KilledWithTimeoutExitCodeAndStderr()
    {
        var runner = new ProcessRunner();
        var sink = new RecordingSink();
        var invocation = new ProcessInvocation(
            executable: "bash",
            arguments: ["-c", "sleep 30"],
            workingDirectory: Directory.GetCurrentDirectory());

        var result = await runner.ExecuteAsync(invocation, sink, TimeSpan.FromSeconds(1), CancellationToken.None);

        result.ExitCode.ShouldBe(124);
        result.Succeeded.ShouldBeFalse();
        string.Join("\n", sink.Stderr).ShouldContain("SQUID_CALAMARI_SCRIPT_TIMEOUT_MINUTES");
    }

    [Fact]
    public async Task ExecuteAsync_ProcessFinishesWithinTimeout_ReturnsRealExitCode()
    {
        var runner = new ProcessRunner();
        var sink = new RecordingSink();
        var invocation = new ProcessInvocation(
            executable: "bash",
            arguments: ["-c", "exit 3"],
            workingDirectory: Directory.GetCurrentDirectory());

        var result = await runner.ExecuteAsync(invocation, sink, TimeSpan.FromMinutes(2), CancellationToken.None);

        result.ExitCode.ShouldBe(3);
    }

    private sealed class RecordingSink : IProcessOutputSink
    {
        public List<string> Stdout { get; } = new();
        public List<string> Stderr { get; } = new();

        public void WriteStdout(string line) => Stdout.Add(line);

        public void WriteStderr(string line) => Stderr.Add(line);
    }
}
