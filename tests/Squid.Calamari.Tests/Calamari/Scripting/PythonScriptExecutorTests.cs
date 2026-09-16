using Shouldly;
using Squid.Calamari.Execution;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Scripting;

/// <summary>
/// PR-10 — unit tests for <see cref="PythonScriptExecutor"/>. Drives the
/// resolver + argument shape via injected seams without requiring Python
/// on the test runner.
/// </summary>
public sealed class PythonScriptExecutorTests
{
    [Fact]
    public void ResolvePythonBinary_NeitherInstalled_ReturnsNull()
    {
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", "/nonexistent-segment");
            PythonScriptExecutor.ResolvePythonBinary().ShouldBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_NoBinaryFound_ThrowsWithOperatorActionableMessage()
    {
        var executor = NewExecutor(new StubProcessRunner(), resolver: () => null);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync("/tmp/x.py", "/tmp", new Squid.Calamari.Execution.ScriptOutputProcessor(), CancellationToken.None));

        ex.Message.ShouldContain("python", Case.Insensitive);
        ex.Message.ShouldContain("Install Python");
        ex.Message.ShouldContain(".sh",
            customMessage: "Error MUST mention the bash-fallback workaround.");
    }

    [Fact]
    public async Task ExecuteAsync_BinaryResolved_InvokesWithUnbufferedFlagAndScriptPath()
    {
        var stub = new StubProcessRunner();
        var executor = NewExecutor(stub, resolver: () => "/usr/bin/python3");

        await executor.ExecuteAsync("/tmp/deploy.py", "/tmp", new Squid.Calamari.Execution.ScriptOutputProcessor(), CancellationToken.None);

        stub.CapturedInvocation.ShouldNotBeNull();
        stub.CapturedInvocation!.Executable.ShouldBe("/usr/bin/python3");
        stub.CapturedInvocation.Arguments.ShouldBe(new[] { "-u", "/tmp/deploy.py" },
            customMessage: "Python MUST run with -u (unbuffered) so logs stream, then the script path.");
        stub.CapturedInvocation.WorkingDirectory.ShouldBe("/tmp");
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesExitCode()
    {
        var stub = new StubProcessRunner { ExitCodeToReturn = 3 };
        var executor = NewExecutor(stub, resolver: () => "/usr/bin/python3");

        var exit = await executor.ExecuteAsync("/tmp/x.py", "/tmp", new Squid.Calamari.Execution.ScriptOutputProcessor(), CancellationToken.None);

        exit.ShouldBe(3);
    }

    private static PythonScriptExecutor NewExecutor(Squid.Calamari.Execution.Processes.IProcessRunner runner, Func<string?> resolver)
        => (PythonScriptExecutor)Activator.CreateInstance(
            typeof(PythonScriptExecutor),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: new object[] { runner, resolver },
            culture: null)!;

    private sealed class StubProcessRunner : Squid.Calamari.Execution.Processes.IProcessRunner
    {
        public int ExitCodeToReturn { get; set; } = 0;
        public Squid.Calamari.Execution.Processes.ProcessInvocation? CapturedInvocation { get; private set; }

        public Task<Squid.Calamari.Execution.Processes.ProcessResult> ExecuteAsync(
            Squid.Calamari.Execution.Processes.ProcessInvocation invocation,
            Squid.Calamari.Execution.Output.IProcessOutputSink outputSink,
            CancellationToken ct)
        {
            CapturedInvocation = invocation;
            return Task.FromResult(new Squid.Calamari.Execution.Processes.ProcessResult(ExitCodeToReturn));
        }
    }
}
