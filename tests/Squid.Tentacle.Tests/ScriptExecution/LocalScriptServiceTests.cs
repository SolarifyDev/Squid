using System;
using System.Diagnostics;
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
        // Path MUST match LocalScriptService.ResolveWorkDir format exactly
        // (`squid-tentacle-{ticketId}` — no `-scripts-` infix). A previous
        // version of this test used the wrong `squid-tentacle-scripts-`
        // prefix and accidentally passed: state file was at path A, code
        // read from path B, path B didn't exist, GetStatus fell through to
        // the unknown-ticket fallback which ALSO returns Complete +
        // UnknownResult — same return, wrong code path. The orphan-detection
        // branch was never exercised. Fixed as part of the P0-T3 audit
        // that added the AlivePid / ZeroPid companions.
        var workDir = Path.Combine(Path.GetTempPath(), $"squid-tentacle-{ticketId}");
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
    public void GetStatus_OrphanStateFile_AlivePid_ReportsRunning_NotComplete()
    {
        // Symmetric companion to the DeadPid test above. Guards the OTHER
        // direction of the orphan-detection predicate.
        //
        // Current implementation (LocalScriptService.cs:269):
        //   if (!state.IsComplete()
        //       && state.ProcessId.HasValue
        //       && state.ProcessId.Value > 0
        //       && !IsProcessAlive(state.ProcessId.Value))
        //     → Complete + UnknownResult
        //
        // The predicate is deliberately NARROW: it only flips to Complete
        // when the PID is DEAD. For a live PID the script is genuinely
        // still running, the tentacle just doesn't have an in-memory
        // entry for it (because the tentacle restarted — e.g. from a
        // prior upgrade). The correct response is Running, so the
        // server-side observer keeps polling.
        //
        // Without this test, a refactor like "any Progress=Running state
        // file seen without a matching in-memory ScriptOrchestrator is
        // stale, report Complete" — a plausible-looking simplification —
        // passes the DeadPid test AND the full test suite, but would
        // reintroduce the observer-hang bug's MIRROR IMAGE: live upgrade
        // scripts would be torn down from the server side before they
        // finish, leaving half-upgraded fleets.
        //
        // We prove the narrowness by handing the predicate a PID we KNOW
        // is alive — the current test process itself — and asserting
        // that GetStatus returns Running, NOT Complete.
        var ticketId = Guid.NewGuid().ToString("N");
        // Path MUST match LocalScriptService.ResolveWorkDir format exactly
        // (`squid-tentacle-{ticketId}` — no `-scripts-` infix). A previous
        // version of this test used the wrong `squid-tentacle-scripts-`
        // prefix and accidentally passed: state file was at path A, code
        // read from path B, path B didn't exist, GetStatus fell through to
        // the unknown-ticket fallback which ALSO returns Complete +
        // UnknownResult — same return, wrong code path. The orphan-detection
        // branch was never exercised. Fixed as part of the P0-T3 audit
        // that added the AlivePid / ZeroPid companions.
        var workDir = Path.Combine(Path.GetTempPath(), $"squid-tentacle-{ticketId}");
        Directory.CreateDirectory(workDir);
        _createdTickets.Add(ticketId);

        var aliveTestProcessPid = Process.GetCurrentProcess().Id;

        var stateStore = new ScriptStateStoreFactory().Create(workDir);
        stateStore.Save(new ScriptState
        {
            TicketId = ticketId,
            Progress = ScriptProgress.Running,
            ProcessId = aliveTestProcessPid,
            CreatedAt = DateTimeOffset.UtcNow
        });

        var status = _service.GetStatus(new ScriptStatusRequest(new ScriptTicket(ticketId), 0));

        status.State.ShouldBe(ProcessState.Running,
            customMessage:
                "state file with Progress=Running + an ALIVE PID must be reported as Running. " +
                "Flipping this to Complete would be the MIRROR-IMAGE of the observer-hang bug: " +
                "instead of the server polling forever on a dead script, the server would " +
                "prematurely conclude a live upgrade script is done. Downstream: Redis lock " +
                "released while script is still mid-restart, FE shows 'upgrade failed' toast " +
                "while the agent is actually restarting fine. Half-upgraded fleets.");
    }

    [Fact]
    public void GetStatus_OrphanStateFile_PidRecycled_ReportsComplete_NotRunning()
    {
        // PID-recycling regression guard (2026-04-24 audit P0-F2).
        //
        // Bug: IsProcessAlive only checks Process.GetProcessById(pid) —
        // Linux PIDs recycle after kernel.pid_max (default 32768 on many
        // distros; 4M on some). A tentacle running for several days with
        // a busy script-execution workload will eventually reuse a PID
        // from an earlier dead upgrade bash script. When the new, unrelated
        // process receives the recycled PID:
        //
        //   1. Old upgrade bash PID=12345 dies during Phase B restart
        //   2. state.json still shows Running + ProcessId=12345
        //   3. systemd (or any user) gets assigned PID 12345 to a fresh
        //      process — common, just how PID allocation works
        //   4. Server polls GetStatus → IsProcessAlive(12345) returns true
        //      (the RECYCLED process is genuinely alive)
        //   5. Orphan-detection does NOT fire → ProcessState.Running
        //   6. Server observer polls forever → Redis lock held → operator
        //      can never retry → UI spinner for the full 5-min timeout
        //
        // This is the observer-hang bug's latent sequel: rare but real
        // in long-running fleet agents.
        //
        // Fix: cross-check ProcessStartedAt in addition to PID liveness.
        // When state.json records a (pid, startTime) pair, a live process
        // at that PID whose actual StartTime differs from the recorded
        // value means PID was recycled — the original script is gone,
        // current occupant of the PID is unrelated.
        //
        // Simulation: write state with ProcessId = current test process
        // PID (guaranteed alive) but ProcessStartedAt = 1970-01-01 (no
        // real process could have that start time on a modern system).
        // Predicate must treat this as recycled → Complete.
        var ticketId = Guid.NewGuid().ToString("N");
        var workDir = Path.Combine(Path.GetTempPath(), $"squid-tentacle-{ticketId}");
        Directory.CreateDirectory(workDir);
        _createdTickets.Add(ticketId);

        var aliveButMismatchedPid = Process.GetCurrentProcess().Id;

        var stateStore = new ScriptStateStoreFactory().Create(workDir);
        stateStore.Save(new ScriptState
        {
            TicketId = ticketId,
            Progress = ScriptProgress.Running,
            ProcessId = aliveButMismatchedPid,
            // Epoch — no real live process on any modern system has this start time.
            ProcessStartedAt = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero),
            CreatedAt = DateTimeOffset.UtcNow
        });

        var status = _service.GetStatus(new ScriptStatusRequest(new ScriptTicket(ticketId), 0));

        status.State.ShouldBe(ProcessState.Complete,
            customMessage:
                "state file records ProcessStartedAt=1970 but the live process at this PID has " +
                "a different actual StartTime — PID has been recycled since the script died. " +
                "Must return Complete + UnknownResult so the observer can release its lock. " +
                "Without this cross-check, the observer-hang bug reappears on long-running agents " +
                "where PID allocation eventually wraps around.");
        status.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult,
            customMessage: "PID-recycled scripts are lost — report UnknownResult (same as dead-PID orphans)");
    }

    [Fact]
    public void GetStatus_OrphanStateFile_PidAndStartTimeMatch_ReportsRunning()
    {
        // Happy-path companion: state file's (ProcessId, ProcessStartedAt)
        // pair MATCHES the actual live process. That's the same script
        // still running — we MUST report Running so the observer keeps
        // polling for real completion.
        //
        // Without this test, a "simplify: always Complete if ProcessStartedAt
        // is set" refactor would pass the recycled-PID test above and
        // silently tear down every live upgrade script.
        var ticketId = Guid.NewGuid().ToString("N");
        var workDir = Path.Combine(Path.GetTempPath(), $"squid-tentacle-{ticketId}");
        Directory.CreateDirectory(workDir);
        _createdTickets.Add(ticketId);

        var currentProc = Process.GetCurrentProcess();

        var stateStore = new ScriptStateStoreFactory().Create(workDir);
        stateStore.Save(new ScriptState
        {
            TicketId = ticketId,
            Progress = ScriptProgress.Running,
            ProcessId = currentProc.Id,
            // Exact actual start time of the current process — no drift.
            ProcessStartedAt = currentProc.StartTime.ToUniversalTime(),
            CreatedAt = DateTimeOffset.UtcNow
        });

        var status = _service.GetStatus(new ScriptStatusRequest(new ScriptTicket(ticketId), 0));

        status.State.ShouldBe(ProcessState.Running,
            customMessage:
                "ProcessId AND ProcessStartedAt both match the live process — same script still " +
                "running. Flipping this to Complete would tear down live upgrade scripts that " +
                "match the recorded (pid, startTime) pair, mirror-imaging the observer-hang bug " +
                "into premature teardown.");
    }

    [Fact]
    public void GetStatus_OrphanStateFile_ZeroPid_ReportsRunning_NotComplete()
    {
        // Edge case on the orphan-detection predicate's left-side guard
        // (state.ProcessId.Value > 0). A zero PID typically means the
        // state file was written before the process ID was captured
        // (ScriptOrchestrator sets it early but there's a narrow startup
        // window). Without the `> 0` guard, IsProcessAlive(0) would be
        // called — Process.GetProcessById(0) throws, !alive=true, and
        // we'd falsely declare the script orphaned.
        //
        // Correct behaviour: when PID is unknown, TRUST the state file's
        // Progress field rather than guessing from liveness. If Running,
        // report Running and let the server observer keep polling — the
        // next state-file update will include the real PID.
        var ticketId = Guid.NewGuid().ToString("N");
        // Path MUST match LocalScriptService.ResolveWorkDir format exactly
        // (`squid-tentacle-{ticketId}` — no `-scripts-` infix). A previous
        // version of this test used the wrong `squid-tentacle-scripts-`
        // prefix and accidentally passed: state file was at path A, code
        // read from path B, path B didn't exist, GetStatus fell through to
        // the unknown-ticket fallback which ALSO returns Complete +
        // UnknownResult — same return, wrong code path. The orphan-detection
        // branch was never exercised. Fixed as part of the P0-T3 audit
        // that added the AlivePid / ZeroPid companions.
        var workDir = Path.Combine(Path.GetTempPath(), $"squid-tentacle-{ticketId}");
        Directory.CreateDirectory(workDir);
        _createdTickets.Add(ticketId);

        var stateStore = new ScriptStateStoreFactory().Create(workDir);
        stateStore.Save(new ScriptState
        {
            TicketId = ticketId,
            Progress = ScriptProgress.Running,
            ProcessId = 0,   // "not yet captured" sentinel
            CreatedAt = DateTimeOffset.UtcNow
        });

        var status = _service.GetStatus(new ScriptStatusRequest(new ScriptTicket(ticketId), 0));

        status.State.ShouldBe(ProcessState.Running,
            customMessage:
                "PID=0 means 'PID not yet captured' — the orphan-detection predicate " +
                "has a `> 0` guard exactly to avoid IsProcessAlive(0) returning false " +
                "and causing a false orphan-declaration. Must return Running based on " +
                "the Progress field alone. If this test starts failing, someone removed " +
                "the `> 0` guard — likely in a refactor trying to 'simplify' the predicate.");
    }

    // ========================================================================
    // TicketId whitelist — P0-T.2 regression guard (2026-04-24 audit).
    //
    // Pre-fix: ResolveWorkDir did Path.Combine(GetTempPath(), $"squid-tentacle-{ticketId}")
    // with no validation of ticketId. Two attack vectors:
    //   1. Absolute path injection — mitigated accidentally by the
    //      `squid-tentacle-` prefix (Path.Combine concats instead of
    //      second-arg-winning once the second arg starts with that
    //      prefix literal).
    //   2. Path traversal — `../../etc` produces `/tmp/squid-tentacle-../../etc`
    //      which normalises to `/etc`. NOT mitigated; server-controlled
    //      ticketId can write agent state, logs, scripts to arbitrary
    //      host paths.
    //
    // Fix: ResolveWorkDir validates ticketId against ^[a-zA-Z0-9_-]{1,64}$
    // and throws on any deviation. Regex is narrow enough to reject
    // `.`, `/`, `\\`, null bytes, whitespace, and Unicode tricks while
    // wide enough to admit every legitimate ticket shape (Guid N-format,
    // SHA256 hex, UUID).
    // ========================================================================

    [Theory]
    [InlineData("abcdef0123456789")]                                         // 16 hex — short guid-like
    [InlineData("0123456789abcdef0123456789abcdef")]                         // 32 hex — Guid.ToString("N")
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")] // 64 hex — SHA256
    [InlineData("upgrade-1-5-8")]                                            // hyphen OK
    [InlineData("task_42")]                                                  // underscore OK
    [InlineData("A")]                                                        // 1-char min
    public void ResolveWorkDir_LegitimateTicketId_ReturnsPathUnderTemp(string ticketId)
    {
        var workDir = LocalScriptService.ResolveWorkDir(ticketId);

        Path.GetFullPath(workDir).ShouldStartWith(Path.GetFullPath(Path.GetTempPath()),
            customMessage:
                "legitimate ticket IDs must resolve to a path UNDER the system temp dir — " +
                "any result outside indicates the prefix isolation or the whitelist has broken");

        workDir.ShouldContain(ticketId);
    }

    [Theory]
    [InlineData("../../etc", "path traversal via `..` segments — the primary attack the whitelist defends against")]
    [InlineData("../../../../../../etc/passwd", "deeper traversal")]
    [InlineData("/etc/passwd", "absolute-path injection — prefix alone is not enough; whitelist also rejects leading slash")]
    [InlineData("..\\..\\Windows", "Windows-style traversal with backslash")]
    [InlineData("foo/bar", "forward slash is a path separator — must be rejected")]
    [InlineData("foo\\bar", "backslash is a Windows path separator")]
    [InlineData("", "empty ticketId means upstream lost the ScriptTicket — fail closed rather than writing to /tmp/squid-tentacle-")]
    [InlineData(" ", "whitespace-only — same rationale as empty")]
    [InlineData("ticket\0evil", "null-byte injection: OS APIs truncate at \\0, any value after is a post-null payload; whitelist rejects")]
    [InlineData("ticket\nnewline", "newline could smuggle a second path or confuse log parsers")]
    [InlineData("ticket with space", "whitespace admits rm-rf-with-space style confusion")]
    [InlineData("foo.bar", "period outside hex range — reject to avoid `.` / `..` being the only problem")]
    [InlineData("тикет", "non-ASCII Unicode — reject (legitimate ticketIds are ASCII hex/guid)")]
    public void ResolveWorkDir_MaliciousTicketId_Throws(string ticketId, string rationale)
    {
        var thrown = Should.Throw<ArgumentException>(
            () => LocalScriptService.ResolveWorkDir(ticketId),
            customMessage:
                $"malicious ticketId must be rejected. Rationale: {rationale}. " +
                $"If this test starts failing, the whitelist regex in LocalScriptService.ResolveWorkDir " +
                $"has been loosened or removed — the P0-T.2 path-traversal vector is re-opened.");

        // The exception message should name the offending input so operators can diagnose log-spam.
        thrown.Message.ShouldContain("ticketId",
            customMessage: "exception must reference the parameter name so the root cause is obvious in logs");
    }

    [Fact]
    public void ResolveWorkDir_TicketIdExceeding64Chars_Throws()
    {
        // 65 chars — one over SHA-256 hex length. No legitimate ticket exceeds 64 chars;
        // this cap prevents DoS-via-long-filename and path-length-limit explosions on some FS.
        var tooLong = new string('a', 65);

        Should.Throw<ArgumentException>(
            () => LocalScriptService.ResolveWorkDir(tooLong),
            customMessage:
                "ticketId > 64 chars must be rejected. Real tickets are at most SHA-256 hex (64 chars); " +
                "longer values indicate either a bug or an attempt to trigger path-length edge cases.");
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

    // P0-T.7 (2026-04-24 audit): WriteAdditionalFiles had an empty catch block that
    // swallowed every write failure (DataStream transfer error, disk full, permission
    // denied, target-path collision, …) and kept iterating. The script then executed
    // against an incomplete workspace and produced silently-wrong results. The fix
    // reroutes cleanup into a finally block and rethrows — the caller must know the
    // workspace is not usable and fail the script start path-fast.

    [Fact]
    public void WriteAdditionalFiles_TargetPathIsDirectory_RethrowsInsteadOfSilentlySucceeding()
    {
        // File.Move can't overwrite a directory — this forces a real exception mid-loop.
        // Pre-fix the empty catch swallowed it and the test's later assertion (no file
        // at target.txt) would pass silently. Post-fix the call itself throws.
        var workDir = Path.Combine(Path.GetTempPath(), $"squid-test-rethrow-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            Directory.CreateDirectory(Path.Combine(workDir, "target.txt"));

            var files = new List<ScriptFile>
            {
                new("target.txt", global::Halibut.DataStream.FromBytes(new byte[] { 1, 2, 3 }), null)
            };

            Should.Throw<Exception>(
                () => LocalScriptService.WriteAdditionalFiles(workDir, files),
                customMessage:
                    "write failures must rethrow. A swallowed exception here lets the script " +
                    "run with a missing input file — silently-wrong deploys. Empty catch is " +
                    "the P0-T.7 vector; regressing reopens it.");
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void WriteAdditionalFiles_FirstFileFails_SubsequentFilesNotWritten()
    {
        // Stronger variant: if the first file fails, we must NOT continue writing
        // subsequent files — the caller needs a clean failure, not a partial workspace.
        var workDir = Path.Combine(Path.GetTempPath(), $"squid-test-rethrow-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            Directory.CreateDirectory(Path.Combine(workDir, "a.txt"));

            var files = new List<ScriptFile>
            {
                new("a.txt", global::Halibut.DataStream.FromBytes(new byte[] { 1 }), null),   // fails
                new("b.txt", global::Halibut.DataStream.FromBytes(new byte[] { 2 }), null),   // would succeed if loop continued
            };

            Should.Throw<Exception>(() => LocalScriptService.WriteAdditionalFiles(workDir, files));

            File.Exists(Path.Combine(workDir, "b.txt")).ShouldBeFalse(
                customMessage:
                    "after the first file write fails the loop must NOT continue — a partial " +
                    "workspace is worse than a clean failure, and the caller sees only the first " +
                    "exception anyway.");
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void WriteAdditionalFiles_WriteFails_TempFileCleanedUp()
    {
        // Cleanup contract: even when we rethrow, the temp file from Path.GetTempFileName
        // must be removed. Otherwise a torrent of failed writes leaks bytes under /tmp.
        var workDir = Path.Combine(Path.GetTempPath(), $"squid-test-rethrow-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        var tempFilesBefore = Directory.GetFiles(Path.GetTempPath(), "tmp*")
            .Select(Path.GetFileName)
            .ToHashSet();

        try
        {
            Directory.CreateDirectory(Path.Combine(workDir, "target.txt"));

            var files = new List<ScriptFile>
            {
                new("target.txt", global::Halibut.DataStream.FromBytes(new byte[] { 1 }), null)
            };

            Should.Throw<Exception>(() => LocalScriptService.WriteAdditionalFiles(workDir, files));

            var tempFilesAfter = Directory.GetFiles(Path.GetTempPath(), "tmp*")
                .Select(Path.GetFileName)
                .ToHashSet();

            var leaked = tempFilesAfter.Except(tempFilesBefore).ToList();

            leaked.ShouldBeEmpty(
                customMessage:
                    $"temp file(s) leaked into {Path.GetTempPath()} after failed write: " +
                    $"[{string.Join(", ", leaked)}]. Cleanup must happen in finally before rethrow.");
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
