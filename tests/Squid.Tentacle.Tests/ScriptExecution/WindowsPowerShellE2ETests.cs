using Shouldly;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Platform;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Collections;
using Xunit;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// Real end-to-end Windows PowerShell tests. Runs a live <see cref="LocalScriptService"/>
/// against the host's bundled pwsh / powershell.exe. Skips on non-Windows so
/// <c>dotnet test</c> on the Linux CI / developer macOS doesn't fail these —
/// nightly GHA workflow <c>tentacle-windows-e2e.yml</c> on <c>windows-latest</c>
/// is the primary execution target.
///
/// These tests fill the gap identified in the post-refactor audit: Windows E2E
/// coverage was zero, leaving the Windows tentacle path untested despite
/// having all the code.
/// </summary>
[Collection(TentacleEnvVarMutatorsCollection.Name)]
[Trait("Category", TentacleTestCategories.WindowsTentacleE2E)]
public sealed class WindowsPowerShellE2ETests : IDisposable
{
    private readonly LocalScriptService _service = new();
    private readonly List<ScriptTicket> _createdTickets = new();

    public void Dispose()
    {
        foreach (var t in _createdTickets)
        {
            try { _service.CancelScript(new CancelScriptCommand(t, 0)); }
            catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Pwsh_SimpleWriteOutput_CapturedInLogs()
    {
        if (!OperatingSystem.IsWindows()) return;

        var command = MakeCommand("Write-Output 'hello-from-pwsh'", ScriptType.PowerShell);

        var startResp = _service.StartScript(command);
        _createdTickets.Add(command.ScriptTicket);

        WaitForCompletion(command.ScriptTicket, TimeSpan.FromSeconds(30));

        var status = _service.GetStatus(new ScriptStatusRequest(command.ScriptTicket, startResp.NextLogSequence));
        var output = string.Join("\n", startResp.Logs.Concat(status.Logs).Select(l => l.Text));

        output.ShouldContain("hello-from-pwsh");
        status.ExitCode.ShouldBe(0);
    }

    [Fact]
    public void Pwsh_NonZeroExitCode_CapturedExactly()
    {
        if (!OperatingSystem.IsWindows()) return;

        var command = MakeCommand("exit 42", ScriptType.PowerShell);

        _service.StartScript(command);
        _createdTickets.Add(command.ScriptTicket);

        WaitForCompletion(command.ScriptTicket, TimeSpan.FromSeconds(30));

        var status = _service.GetStatus(new ScriptStatusRequest(command.ScriptTicket, 0));
        status.ExitCode.ShouldBe(42);
    }

    [Fact]
    public void Pwsh_StdErrOutput_TaggedAsErrorSource()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Write-Error sends to stderr pipeline; LocalScriptService routes those
        // to ProcessOutputSource.StdErr.
        var command = MakeCommand("Write-Error 'boom from test'; exit 0", ScriptType.PowerShell);

        var startResp = _service.StartScript(command);
        _createdTickets.Add(command.ScriptTicket);

        WaitForCompletion(command.ScriptTicket, TimeSpan.FromSeconds(30));

        var status = _service.GetStatus(new ScriptStatusRequest(command.ScriptTicket, startResp.NextLogSequence));
        var allLogs = startResp.Logs.Concat(status.Logs).ToList();

        allLogs.ShouldContain(l => l.Source == ProcessOutputSource.StdErr && l.Text.Contains("boom from test"),
            "Write-Error must be tagged as StdErr so operators can distinguish agent output from errors");
    }

    [Fact]
    public void Pwsh_UnicodeOutput_EncodedCorrectly()
    {
        if (!OperatingSystem.IsWindows()) return;

        var command = MakeCommand("Write-Output '你好 deployment 🚀'", ScriptType.PowerShell);

        var startResp = _service.StartScript(command);
        _createdTickets.Add(command.ScriptTicket);

        WaitForCompletion(command.ScriptTicket, TimeSpan.FromSeconds(30));

        var status = _service.GetStatus(new ScriptStatusRequest(command.ScriptTicket, startResp.NextLogSequence));
        var output = string.Join("\n", startResp.Logs.Concat(status.Logs).Select(l => l.Text));

        // Allows for the rocket to be mangled in some PowerShell console encodings;
        // we only require the Chinese characters to round-trip, which proves UTF-8
        // is being honoured for stdout.
        output.ShouldContain("你好");
        output.ShouldContain("deployment");
    }

    [Fact]
    public void Pwsh_OptInWindowsPowerShellExe_ViaEnvVar_RunsSuccessfully()
    {
        // P1-Phase12.B.3 — the operator opt-in path: setting
        // SQUID_TENTACLE_USE_WINDOWS_POWERSHELL=true routes Phase-12.B's
        // factory dispatch to WindowsPowerShellProcessLauncher (which targets
        // the OS-bundled PowerShell.exe). This E2E proves the env var →
        // factory → launcher → Process.Start chain actually works on real
        // Windows. ASCII-only assertion: cross-locale Unicode via OEM is
        // locale-dependent (Pwsh_UnicodeOutput_EncodedCorrectly above proves
        // the default pwsh-Core path with UTF-8); this test only exercises
        // the wiring + exit-code propagation through PowerShell.exe.
        if (!OperatingSystem.IsWindows()) return;

        var prior = Environment.GetEnvironmentVariable(ProcessLauncherFactory.UseWindowsPowerShellEnvVar);

        try
        {
            Environment.SetEnvironmentVariable(ProcessLauncherFactory.UseWindowsPowerShellEnvVar, "true");

            var command = MakeCommand("Write-Output 'hello-from-windows-powershell-exe'", ScriptType.PowerShell);

            var startResp = _service.StartScript(command);
            _createdTickets.Add(command.ScriptTicket);

            WaitForCompletion(command.ScriptTicket, TimeSpan.FromSeconds(30));

            var status = _service.GetStatus(new ScriptStatusRequest(command.ScriptTicket, startResp.NextLogSequence));
            var output = string.Join("\n", startResp.Logs.Concat(status.Logs).Select(l => l.Text));

            output.ShouldContain("hello-from-windows-powershell-exe");
            status.ExitCode.ShouldBe(0);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProcessLauncherFactory.UseWindowsPowerShellEnvVar, prior);
        }
    }

    private static StartScriptCommand MakeCommand(string scriptBody, ScriptType syntax)
    {
        return new StartScriptCommand(
            new ScriptTicket(Guid.NewGuid().ToString("N")),
            scriptBody,
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(5),
            null,
            Array.Empty<string>(),
            null,
            TimeSpan.Zero)
        {
            ScriptSyntax = syntax
        };
    }

    private void WaitForCompletion(ScriptTicket ticket, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = _service.GetStatus(new ScriptStatusRequest(ticket, 0));
            if (status.State == ProcessState.Complete) return;
            Thread.Sleep(100);
        }
    }
}
