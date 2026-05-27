using Shouldly;
using Squid.Calamari.Execution;
using Squid.Calamari.Scripting;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Scripting;

/// <summary>
/// PR-4 — unit-level tests for <see cref="PowerShellScriptExecutor"/>.
/// Drives the resolver + argument shape via injectable seams without
/// requiring <c>pwsh</c> on the test runner (which would fail on CI hosts
/// that don't have PS Core installed).
/// </summary>
public sealed class PowerShellScriptExecutorTests
{
    [Fact]
    public void ResolvePowerShellBinary_NeitherInstalled_ReturnsNull()
    {
        // Pin the contract: when PATH contains neither pwsh nor powershell.exe,
        // resolver returns null and the caller throws a clear error.
        // (We can't easily "uninstall" pwsh for the test, so this test runs
        // with a curated PATH set via env var.)
        var originalPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            Environment.SetEnvironmentVariable("PATH", "/nonexistent-segment");
            PowerShellScriptExecutor.ResolvePowerShellBinary().ShouldBeNull(
                customMessage: "With no pwsh or powershell.exe on the PATH, resolver MUST return null. " +
                               "Caller surfaces 'PowerShell not installed' error to operator.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_NoBinaryFound_ThrowsWithOperatorActionableMessage()
    {
        // Inject a null-resolver and verify the operator-facing error message
        // names the install URL + the bash-fallback path.
        var executor = new TestableExecutor(processRunner: new StubProcessRunner(), resolver: () => null);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            executor.RunAsync("/tmp/x.ps1", "/tmp", new Squid.Calamari.Execution.ScriptOutputProcessor(), CancellationToken.None));

        ex.Message.ShouldContain("pwsh", Case.Insensitive);
        ex.Message.ShouldContain("install",
            customMessage: "Error MUST name how to install PowerShell — operator needs the next step, not just the diagnosis.");
        ex.Message.ShouldContain(".sh",
            customMessage: "Error MUST mention bash-fallback path so operator knows the workaround.");
    }

    [Fact]
    public async Task ExecuteAsync_BinaryResolved_InvokesWithCorrectArguments()
    {
        // Pin the argument shape: -NoProfile -NonInteractive -File <path>.
        // -NoProfile defends against operator's profile.ps1 polluting env;
        // -NonInteractive refuses Read-Host prompts that would hang the deploy.
        var stubProcess = new StubProcessRunner();
        var executor = new TestableExecutor(processRunner: stubProcess, resolver: () => "/usr/bin/pwsh");

        await executor.RunAsync("/tmp/script.ps1", "/tmp", new Squid.Calamari.Execution.ScriptOutputProcessor(), CancellationToken.None);

        stubProcess.CapturedInvocation.ShouldNotBeNull();
        stubProcess.CapturedInvocation!.Executable.ShouldBe("/usr/bin/pwsh");
        stubProcess.CapturedInvocation.Arguments.ShouldBe(new[]
        {
            "-NoProfile",
            "-NonInteractive",
            "-File",
            "/tmp/script.ps1"
        });
        stubProcess.CapturedInvocation.WorkingDirectory.ShouldBe("/tmp");
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesExitCodeFromProcess()
    {
        var stubProcess = new StubProcessRunner { ExitCodeToReturn = 42 };
        var executor = new TestableExecutor(processRunner: stubProcess, resolver: () => "/usr/bin/pwsh");

        var exitCode = await executor.RunAsync("/tmp/x.ps1", "/tmp", new Squid.Calamari.Execution.ScriptOutputProcessor(), CancellationToken.None);

        exitCode.ShouldBe(42);
    }

    // ── Test seam wrapping the internal ctor with the injected resolver ─────

    private sealed class TestableExecutor
    {
        private readonly PowerShellScriptExecutor _inner;
        public TestableExecutor(Squid.Calamari.Execution.Processes.IProcessRunner processRunner, Func<string?> resolver)
        {
            // Reach the internal constructor that accepts the resolver func.
            _inner = (PowerShellScriptExecutor)Activator.CreateInstance(
                typeof(PowerShellScriptExecutor),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                binder: null,
                args: new object[] { processRunner, resolver },
                culture: null)!;
        }

        public Task<int> RunAsync(string scriptPath, string workDir, Squid.Calamari.Execution.ScriptOutputProcessor processor, CancellationToken ct)
            => _inner.ExecuteAsync(scriptPath, workDir, processor, ct);
    }

    /// <summary>Stub <see cref="Execution.Processes.IProcessRunner"/> that captures the invocation + returns a configurable exit code.</summary>
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
