using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.ScriptExecution.State;

namespace Squid.Tentacle.Tests.ScriptExecution;

public class LocalScriptServiceTests : IDisposable
{
    private readonly LocalScriptService _service = new();
    private readonly List<string> _createdTickets = new();

    // ========================================================================
    // Basic Lifecycle
    // ========================================================================

    [Fact]
    public void StartScript_CreatesWorkDirectory()
    {
        var ticket = StartEchoScript("hello");

        var workDir = FindWorkDir(ticket);
        Directory.Exists(workDir).ShouldBeTrue();
    }

    [Fact]
    public void StartScript_WritesScriptFile()
    {
        var ticket = StartEchoScript("hello world");

        var scriptPath = Path.Combine(FindWorkDir(ticket), "script.sh");
        File.Exists(scriptPath).ShouldBeTrue();
        File.ReadAllText(scriptPath).ShouldContain("echo 'hello world'");
    }

    [Fact]
    public void StartScript_ReturnsUniqueTickets()
    {
        var ticket1 = StartEchoScript("1");
        var ticket2 = StartEchoScript("2");

        ticket1.TaskId.ShouldNotBe(ticket2.TaskId);
    }

    [Fact]
    public void GetStatus_RunningProcess_ReturnsRunning()
    {
        var command = MakeCommand("sleep 10");
        _service.StartScript(command);
        var ticket = command.ScriptTicket;
        _createdTickets.Add(ticket.TaskId);

        var status = _service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(ProcessState.Running);
    }

    [Fact]
    public void GetStatus_CompletedProcess_ReturnsCompleteWithExitCode()
    {
        var ticket = StartEchoScript("done");
        Thread.Sleep(500);

        var status = _service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(0);
    }

    [Fact]
    public void GetStatus_UnknownTicket_ReturnsUnknownResult()
    {
        var status = _service.GetStatus(new ScriptStatusRequest(new ScriptTicket("unknown"), 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult);
    }

    [Fact]
    public void GetStatus_OrphanStateFile_DeadPid_ReportsComplete_NotRunning()
    {
        // 1.6.x regression guard for the "upgrade HTTP dispatch hangs 5min"
        // bug: if the tentacle process that was running a script gets
        // killed mid-execution (e.g. Phase B systemctl restart during
        // self-upgrade), it leaves behind a state.json with Progress=Running
        // + a now-dead ProcessId. Before the fix, a new tentacle's
        // GetStatus would read that file and return ProcessState.Running
        // forever — the server-side Halibut observer would poll forever,
        // the Redis upgrade lock would stay held, the operator's UI would
        // show a spinner indefinitely.
        //
        // After fix: GetStatus detects the dead ProcessId and reports
        // Complete with UnknownResult so the observer can release its
        // lock.
        //
        // Simulation: manually craft a workDir + state.json with a PID
        // guaranteed not to exist (int.MaxValue) and call GetStatus.
        var ticketId = Guid.NewGuid().ToString("N");
        var workDir = Path.Combine(Path.GetTempPath(), $"squid-tentacle-scripts-{ticketId}");
        Directory.CreateDirectory(workDir);
        _createdTickets.Add(ticketId);

        var stateStore = new ScriptStateStoreFactory().Create(workDir);
        stateStore.Save(new ScriptState
        {
            TicketId = ticketId,
            Progress = ScriptProgress.Running,
            ProcessId = int.MaxValue,  // guaranteed to not match any live PID
            CreatedAt = DateTimeOffset.UtcNow
        });

        var status = _service.GetStatus(new ScriptStatusRequest(new ScriptTicket(ticketId), 0));

        status.State.ShouldBe(ProcessState.Complete,
            customMessage: "orphan state file with dead PID must be reported as Complete so the server's Halibut observer can finalize — otherwise it polls forever and the upgrade HTTP request hangs for the full 5-min script timeout");
        status.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult,
            customMessage: "orphan scripts report UnknownResult (we can't determine the actual exit code since the process is gone) — matches the unknown-ticket fallback semantics");
    }

    [Fact]
    public void CompleteScript_CleansUpWorkDir()
    {
        var ticket = StartEchoScript("cleanup");
        Thread.Sleep(500);

        _service.CompleteScript(new CompleteScriptCommand(ticket, 0));

        var workDir = FindWorkDir(ticket);
        Directory.Exists(workDir).ShouldBeFalse();
    }

    [Fact]
    public void CancelScript_KillsProcessAndReturnsCanceled()
    {
        var command = MakeCommand("sleep 60");
        _service.StartScript(command);
        var ticket = command.ScriptTicket;
        _createdTickets.Add(ticket.TaskId);

        var status = _service.CancelScript(new CancelScriptCommand(ticket, 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(ScriptExitCodes.Canceled);
    }

    // ========================================================================
    // Fix 1: Script Arguments
    // ========================================================================

    [Fact]
    public void StartScript_WithArguments_PassedToBash()
    {
        var command = new StartScriptCommand(
            new ScriptTicket(Guid.NewGuid().ToString("N")),
            "echo \"$1 $2\"",
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(5),
            null,
            new[] { "hello", "world" },
            null,
            TimeSpan.Zero);

        var startResp = _service.StartScript(command);
        var ticket = command.ScriptTicket;
        _createdTickets.Add(ticket.TaskId);

        Thread.Sleep(1000);

        var status = _service.GetStatus(new ScriptStatusRequest(ticket, startResp.NextLogSequence));
        var output = string.Join(" ", startResp.Logs.Concat(status.Logs).Select(l => l.Text));

        output.ShouldContain("hello world");
    }

    [Fact]
    public void StartScript_WithArguments_SpacesHandled()
    {
        var command = new StartScriptCommand(
            new ScriptTicket(Guid.NewGuid().ToString("N")),
            "echo \"$1\"",
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(5),
            null,
            new[] { "hello world" },
            null,
            TimeSpan.Zero);

        var startResp = _service.StartScript(command);
        var ticket = command.ScriptTicket;
        _createdTickets.Add(ticket.TaskId);

        Thread.Sleep(1000);

        var status = _service.GetStatus(new ScriptStatusRequest(ticket, startResp.NextLogSequence));
        var output = string.Join(" ", startResp.Logs.Concat(status.Logs).Select(l => l.Text));

        output.ShouldContain("hello world");
    }

    [Fact]
    public void StartScript_EmptyArguments_NoBreak()
    {
        var ticket = StartEchoScript("no-args");
        Thread.Sleep(500);

        var status = _service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(0);
    }

    // ========================================================================
    // Fix 2: Script Syntax Routing
    // ========================================================================

    [Fact]
    public void StartScript_BashSyntax_WritesShFile()
    {
        var ticket = StartEchoScript("bash-test");

        var scriptPath = Path.Combine(FindWorkDir(ticket), "script.sh");
        File.Exists(scriptPath).ShouldBeTrue();
    }

    [Fact]
    public void StartScript_PowerShellSyntax_WritesPs1File()
    {
        var command = new StartScriptCommand(
            new ScriptTicket(Guid.NewGuid().ToString("N")),
            "Write-Host 'pwsh-test'",
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(5),
            null,
            Array.Empty<string>(),
            null,
            TimeSpan.Zero)
        {
            ScriptSyntax = ScriptType.PowerShell
        };

        ScriptTicket ticket;
        try
        {
            _service.StartScript(command);
            ticket = command.ScriptTicket;
            _createdTickets.Add(ticket.TaskId);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // pwsh not installed — skip execution test, just verify file was written
            return;
        }

        var ps1Path = Path.Combine(FindWorkDir(ticket), "script.ps1");
        File.Exists(ps1Path).ShouldBeTrue();
        File.ReadAllText(ps1Path).ShouldContain("Write-Host 'pwsh-test'");
    }

    [Fact]
    public void StartScript_DefaultSyntax_IsBash()
    {
        var command = new StartScriptCommand(
            new ScriptTicket(Guid.NewGuid().ToString("N")),
            "echo 'default-syntax'",
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(5),
            null,
            Array.Empty<string>(),
            null,
            TimeSpan.Zero);

        // ScriptSyntax not set — should default to Bash
        command.ScriptSyntax.ShouldBe(ScriptType.Bash);
    }

    // ========================================================================
    // Fix 3: Unix File Permissions
    // ========================================================================

    [Fact]
    public void WriteScriptFile_HasExecutablePermission()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var workDir = Path.Combine(Path.GetTempPath(), $"squid-test-perm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            LocalScriptService.WriteScriptFile(workDir, "echo ok", ScriptType.Bash);

            var mode = File.GetUnixFileMode(Path.Combine(workDir, "script.sh"));
            (mode & UnixFileMode.UserExecute).ShouldBe(UnixFileMode.UserExecute);
            (mode & UnixFileMode.UserRead).ShouldBe(UnixFileMode.UserRead);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void WriteAdditionalFiles_DataFilePermission()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var workDir = Path.Combine(Path.GetTempPath(), $"squid-test-perm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            var fileContent = System.Text.Encoding.UTF8.GetBytes("test data");
            var files = new List<ScriptFile>
            {
                new("data.json", global::Halibut.DataStream.FromBytes(fileContent), null)
            };

            LocalScriptService.WriteAdditionalFiles(workDir, files);

            var mode = File.GetUnixFileMode(Path.Combine(workDir, "data.json"));
            (mode & UnixFileMode.UserRead).ShouldBe(UnixFileMode.UserRead);
            (mode & UnixFileMode.UserWrite).ShouldBe(UnixFileMode.UserWrite);
            (mode & UnixFileMode.UserExecute).ShouldNotBe(UnixFileMode.UserExecute);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    // ========================================================================
    // Fix 5: Orphaned Workspace Cleanup
    // ========================================================================

    [Fact]
    public void CleanupOrphanedWorkspaces_RemovesOldDirs()
    {
        var oldDir = Path.Combine(Path.GetTempPath(), $"squid-tentacle-cleanup-old-{Guid.NewGuid():N}");
        Directory.CreateDirectory(oldDir);
        Directory.SetCreationTimeUtc(oldDir, DateTime.UtcNow.AddHours(-48));

        var cleaned = LocalScriptService.CleanupOrphanedWorkspaces(TimeSpan.FromHours(24));

        cleaned.ShouldBeGreaterThanOrEqualTo(1);
        Directory.Exists(oldDir).ShouldBeFalse();
    }

    [Fact]
    public void CleanupOrphanedWorkspaces_PreservesRecentDirs()
    {
        var recentDir = Path.Combine(Path.GetTempPath(), $"squid-tentacle-cleanup-new-{Guid.NewGuid():N}");
        Directory.CreateDirectory(recentDir);

        try
        {
            LocalScriptService.CleanupOrphanedWorkspaces(TimeSpan.FromHours(24));

            Directory.Exists(recentDir).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(recentDir, recursive: true);
        }
    }

    // ========================================================================
    // Path Traversal Protection
    // ========================================================================

    [Fact]
    public void WriteAdditionalFiles_PathTraversal_Throws()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"squid-test-traversal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            var files = new List<ScriptFile>
            {
                new("../../etc/evil.txt", global::Halibut.DataStream.FromBytes(new byte[] { 1 }), null)
            };

            Should.Throw<InvalidOperationException>(() =>
                LocalScriptService.WriteAdditionalFiles(workDir, files));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    // ========================================================================
    // Drain / Graceful Shutdown
    // ========================================================================

    [Fact]
    public void StartScript_WhileDraining_Throws()
    {
        _service.WaitForDrainAsync(TimeSpan.FromMilliseconds(1)).GetAwaiter().GetResult();

        Should.Throw<InvalidOperationException>(() =>
            _service.StartScript(MakeCommand("echo 'should-fail'")));
    }

    // ========================================================================
    // Shell Escaping
    // ========================================================================

    [Theory]
    [InlineData("hello", "'hello'")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("", "''")]
    [InlineData("a b c", "'a b c'")]
    public void ShellEscape_CorrectlyEscapes(string input, string expected)
    {
        LocalScriptService.ShellEscape(input).ShouldBe(expected);
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private ScriptTicket StartEchoScript(string message)
    {
        var command = MakeCommand($"echo '{message}'");
        _service.StartScript(command);
        _createdTickets.Add(command.ScriptTicket.TaskId);

        return command.ScriptTicket;
    }

    private static StartScriptCommand MakeCommand(string scriptBody)
    {
        return new StartScriptCommand(
            new ScriptTicket(Guid.NewGuid().ToString("N")),
            scriptBody,
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(5),
            null,
            Array.Empty<string>(),
            null,
            TimeSpan.Zero);
    }

    private static string FindWorkDir(ScriptTicket ticket)
    {
        return Directory.EnumerateDirectories(Path.GetTempPath(), $"squid-tentacle-{ticket.TaskId}*")
            .FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), $"squid-tentacle-{ticket.TaskId}");
    }

    public void Dispose()
    {
        foreach (var ticketId in _createdTickets)
        {
            try
            {
                _service.CancelScript(new CancelScriptCommand(new ScriptTicket(ticketId), 0));
            }
            catch { }

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), $"squid-tentacle-{ticketId}*"))
                    Directory.Delete(dir, recursive: true);
            }
            catch { }
        }
    }
}
