using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Squid.Core.Services.Machines.Upgrade;

namespace Squid.WindowsUpgradeE2ETests;

/// <summary>
/// end-to-end verification of the outer-wrapper
/// detach mechanism on a real Windows host. The unit tests in
/// <c>WindowsTentacleUpgradeStrategyTests</c> already pin the wrapper SHAPE
/// (contains schtasks, /RU SYSTEM, /Z, GUID task name, base64-encoded inner,
/// etc.); this suite verifies REAL EXECUTION behaviour:
///
/// <list type="number">
///   <item>The wrapper actually exits 0 when run via real <c>powershell.exe</c>
///         under elevated identity (the GHA <c>windows-latest</c> runner
///         gives admin rights by default).</item>
///   <item>The dispatched inner script actually runs in a separate Task
///         Scheduler process tree as SYSTEM, surviving the wrapper's exit.</item>
///   <item><c>/Z</c> auto-delete actually removes the task from the Task
///         Scheduler library after run.</item>
///   <item>Concurrent dispatches with GUID-suffixed task names don't collide.</item>
///   <item>Inner-script failure does NOT propagate to the wrapper's exit code
///         (Halibut sees the wrapper's outcome, NOT the detached inner's).</item>
/// </list>
///
/// <para><b>Why a dedicated E2E project (not Tentacle.Tests / UnitTests):</b>
/// the wrapper is generated server-side (<c>Squid.Core.WindowsTentacleUpgradeStrategy</c>)
/// and EXECUTED on a Windows host — neither test project is the right home.
/// Tentacle.Tests doesn't reference Squid.Core; UnitTests doesn't run on
/// windows-latest by default. This project plugs into the existing
/// <c>tentacle-windows-e2e.yml</c> workflow as a separate filter step.</para>
///
/// <para><b>Skip-on-non-Windows:</b> every test no-ops cleanly when run on
/// macOS/Linux dev boxes, mirroring the established pattern in
/// <c>Squid.Tentacle.Tests.ScriptExecution.WindowsPowerShellE2ETests</c>.</para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.Wrapper)]
public sealed class WindowsUpgradeWrapperE2ETests : IDisposable
{
    private readonly List<string> _scheduledTaskNamesToCleanup = new();
    private readonly List<string> _markerFilesToCleanup = new();
    private readonly List<string> _tempScriptsToCleanup = new();

    /// <summary>
    /// Best-effort cleanup. Even if a test fails partway, we MUST drop
    /// scheduled tasks (Task Scheduler library accumulates otherwise) and
    /// remove temp files. <c>schtasks /Delete /F</c> is idempotent — already-
    /// gone tasks return non-zero but no exception, which the try/catch
    /// swallows.
    /// </summary>
    public void Dispose()
    {
        if (!OperatingSystem.IsWindows()) return;

        foreach (var taskName in _scheduledTaskNamesToCleanup)
            TryDeleteTask(taskName);

        foreach (var path in _markerFilesToCleanup)
            TrySafeDelete(path);

        foreach (var path in _tempScriptsToCleanup)
            TrySafeDelete(path);

        //  strategy writes dispatch-<TaskName>.ps1 to %ProgramData%\Squid\Tentacle\upgrade\
        // (per-task — concurrent-dispatch race fix). Each test's per-task
        // dispatch file is added to _tempScriptsToCleanup so they're
        // cleaned at Dispose; the wrapper itself also runs a >1h cleanup
        // pass at the start of every new dispatch so accumulation is bounded.
    }

    // ========================================================================
    // Test 1: Wrapper exits cleanly + per-task dispatch-<TaskName>.ps1 written + task registered
    // ========================================================================

    [Fact]
    public void Wrapper_WhenRunOnRealWindows_ExitsZeroAndWritesDispatchPs1()
    {
        if (!OperatingSystem.IsWindows()) return;

        var markerPath = MakeUniqueMarkerPath("smoke");
        var inner = BuildMarkerInnerScript(markerPath, "smoke-test-marker");
        var wrapper = BuildWrapperFor(inner);

        var (exitCode, stdout) = RunWrapper(wrapper, TimeSpan.FromSeconds(30));

        exitCode.ShouldBe(0,
            customMessage: $"wrapper must exit 0 after schtasks /Run; stdout was: {stdout}");

        var taskName = ExtractTaskName(stdout);
        _scheduledTaskNamesToCleanup.Add(taskName);

        // dispatch file is now per-task (dispatch-<TaskName>.ps1) so concurrent
        // wrappers can't race on a shared file. Verify the per-task dispatch
        // file exists in the contract dir.
        var dispatchPath = GetExpectedDispatchPath(taskName);
        _tempScriptsToCleanup.Add(dispatchPath);
        File.Exists(dispatchPath).ShouldBeTrue(
            customMessage: $"wrapper must write inner to {dispatchPath}");

        // The inner script's actual execution is verified in Test 2; here we
        // just pin that the scheduling step itself reported success.
        stdout.ShouldContain("registered",
            customMessage: "wrapper must log task registration before exiting");
        stdout.ShouldContain("triggered",
            customMessage: "wrapper must log task trigger after schtasks /Run");
    }

    // ========================================================================
    // Test 2: Detached inner runs as SYSTEM, AFTER wrapper exits
    // (the load-bearing Phase B survival invariant — only E2E can verify this)
    // ========================================================================

    [Fact]
    public void Wrapper_DetachedTask_RunsAsSystemAfterWrapperExits()
    {
        if (!OperatingSystem.IsWindows()) return;

        var markerPath = MakeUniqueMarkerPath("identity");

        // Inner records WindowsIdentity so we can prove SYSTEM context.
        // `whoami` returns "nt authority\system" when running under /RU SYSTEM.
        var inner = $@"
$ErrorActionPreference = 'Stop'
$whoami = whoami
Set-Content -Path '{markerPath}' -Value $whoami -Encoding UTF8 -Force
exit 0
";

        var wrapper = BuildWrapperFor(inner);
        var (exitCode, stdout) = RunWrapper(wrapper, TimeSpan.FromSeconds(30));

        exitCode.ShouldBe(0);
        var taskName = ExtractTaskName(stdout);
        _scheduledTaskNamesToCleanup.Add(taskName);
        _markerFilesToCleanup.Add(markerPath);

        // The detached task runs ASYNCHRONOUSLY — wrapper has already exited
        // by the time we look for the marker. Poll with reasonable timeout
        // (Task Scheduler launch latency on healthy GHA runners is 1-5s; we
        // give 60s headroom for slow runners under load).
        WaitForFileExists(markerPath, TimeSpan.FromSeconds(60))
            .ShouldBeTrue(
                customMessage: $"marker {markerPath} did NOT appear within 60s — Task Scheduler did not launch the inner script (likely schtasks /Run silently failed, or /RU SYSTEM was rejected by the runner's security policy)");

        var content = File.ReadAllText(markerPath).Trim();

        content.ShouldStartWith("nt authority",
            Case.Insensitive,
            customMessage: $"inner ran as '{content}' but expected 'nt authority\\system' (proves /RU SYSTEM took effect — without this, schtasks created the task with WRONG identity which would fail Phase B's Stop-Service / Move-Item)");
        content.ShouldContain("system",
            Case.Insensitive,
            customMessage: $"inner identity must be SYSTEM (was: '{content}')");
    }

    // ========================================================================
    // Test 3: /Z auto-delete actually removes the task
    // ========================================================================

    [Fact]
    public void Wrapper_ScheduledTask_AutoDeletesAfterRun_ZFlag()
    {
        if (!OperatingSystem.IsWindows()) return;

        var markerPath = MakeUniqueMarkerPath("autodelete");
        var inner = BuildMarkerInnerScript(markerPath, "autodelete-test");
        var wrapper = BuildWrapperFor(inner);

        var (_, stdout) = RunWrapper(wrapper, TimeSpan.FromSeconds(30));
        var taskName = ExtractTaskName(stdout);
        _scheduledTaskNamesToCleanup.Add(taskName);  // safety net
        _markerFilesToCleanup.Add(markerPath);

        // Wait for inner to complete (marker appears).
        WaitForFileExists(markerPath, TimeSpan.FromSeconds(60))
            .ShouldBeTrue("inner must complete before we check auto-delete");

        // Task Scheduler /Z deletes "after expiration". A one-shot run with
        // /SC ONCE expires immediately after the run completes, so the delete
        // typically happens within seconds. Poll up to 60s.
        WaitForTaskGone(taskName, TimeSpan.FromSeconds(60))
            .ShouldBeTrue(
                customMessage: $"task '{taskName}' should auto-delete via /Z after run completes; Task Scheduler library would otherwise accumulate orphan upgrade tasks across every dispatch");
    }

    // ========================================================================
    // Test 4: Concurrent dispatches with GUID-suffixed task names don't collide
    // ========================================================================

    [Fact]
    public void Wrapper_ConcurrentDispatches_GuidSuffixedTaskNames_DoNotCollide()
    {
        if (!OperatingSystem.IsWindows()) return;

        var markerA = MakeUniqueMarkerPath("concurrent-a");
        var markerB = MakeUniqueMarkerPath("concurrent-b");

        var innerA = BuildMarkerInnerScript(markerA, "dispatch-a");
        var innerB = BuildMarkerInnerScript(markerB, "dispatch-b");

        var wrapperA = BuildWrapperFor(innerA);
        var wrapperB = BuildWrapperFor(innerB);

        // Launch in parallel. If GUID suffixing is broken (e.g. someone
        // refactors to use a fixed name), one task /Create /F would overwrite
        // the other before run, and only one marker would appear.
        var taskA = Task.Run(() => RunWrapper(wrapperA, TimeSpan.FromSeconds(30)));
        var taskB = Task.Run(() => RunWrapper(wrapperB, TimeSpan.FromSeconds(30)));
        Task.WaitAll(taskA, taskB);

        var (exitA, stdoutA) = taskA.Result;
        var (exitB, stdoutB) = taskB.Result;

        exitA.ShouldBe(0);
        exitB.ShouldBe(0);

        var nameA = ExtractTaskName(stdoutA);
        var nameB = ExtractTaskName(stdoutB);
        _scheduledTaskNamesToCleanup.Add(nameA);
        _scheduledTaskNamesToCleanup.Add(nameB);
        _markerFilesToCleanup.Add(markerA);
        _markerFilesToCleanup.Add(markerB);

        nameA.ShouldNotBe(nameB,
            "concurrent dispatches MUST get distinct task names — without GUID suffixing, /F would overwrite the earlier task before its run trigger");

        // BOTH inners must run independently. If GUID suffixing were broken,
        // one of the markers would be missing.
        WaitForFileExists(markerA, TimeSpan.FromSeconds(60)).ShouldBeTrue(
            "inner A's marker must appear — proves task A actually ran");
        WaitForFileExists(markerB, TimeSpan.FromSeconds(60)).ShouldBeTrue(
            "inner B's marker must appear — proves task B actually ran (and wasn't overwritten by A's /F /Create)");
    }

    // ========================================================================
    // Test 5: Inner-script failure does NOT propagate to wrapper exit code
    // (Halibut MUST see "wrapper succeeded" so the strategy maps to Initiated;
    // actual upgrade outcome arrives via last-upgrade.json on next health check)
    // ========================================================================

    [Fact]
    public void Wrapper_InnerExitsNonZero_WrapperStillExitsZero()
    {
        if (!OperatingSystem.IsWindows()) return;

        var markerPath = MakeUniqueMarkerPath("inner-fail");

        // Inner writes marker (so we can prove it ran), THEN exits non-zero.
        // The wrapper itself doesn't observe this exit — Task Scheduler captures
        // it asynchronously. The wrapper has already returned its own /Run
        // success or failure to Halibut.
        var inner = $@"
$ErrorActionPreference = 'Stop'
Set-Content -Path '{markerPath}' -Value 'inner-ran-then-failed' -Encoding UTF8 -Force
exit 99
";

        var wrapper = BuildWrapperFor(inner);
        var (exitCode, stdout) = RunWrapper(wrapper, TimeSpan.FromSeconds(30));

        exitCode.ShouldBe(0,
            customMessage: "wrapper exit code must reflect schtasks /Run success, NOT the detached inner's later exit. Otherwise the strategy would map a Phase B upgrade failure (rare but possible) to a Halibut Failed status, when the architecturally correct status is Initiated (the rapid-polling burst reads last-upgrade.json to find the real outcome)");

        var taskName = ExtractTaskName(stdout);
        _scheduledTaskNamesToCleanup.Add(taskName);
        _markerFilesToCleanup.Add(markerPath);

        WaitForFileExists(markerPath, TimeSpan.FromSeconds(60)).ShouldBeTrue(
            "inner must have run (marker written) before its exit 99 — proves Task Scheduler launched it despite the eventual failure");
    }

    // ========================================================================
    // Helpers — deliberately kept simple (no external dependencies, no fixture
    // class). Each helper is one short responsibility so future Linux-host or
    // alternate-detach-mechanism E2E tests can copy the pattern.
    // ========================================================================

    /// <summary>
    /// Wraps the production strategy's <c>BuildOuterWrapper</c> with a
    /// caller-supplied inner. The strategy is server-side code; this E2E
    /// test reaches into the internal <c>BuildOuterWrapper(string)</c> seam
    /// to substitute a test marker inner instead of the full
    /// <c>upgrade-windows-tentacle.ps1</c> template (which has its own unit
    /// coverage for shape).
    /// </summary>
    private static string BuildWrapperFor(string innerPowerShell)
    {
        var innerBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(innerPowerShell));
        return WindowsTentacleUpgradeStrategy.BuildOuterWrapper(innerBase64);
    }

    /// <summary>
    /// Tiny inner that writes a marker file then exits 0. Used by the
    /// scheduling-only tests that don't care about specific inner behaviour.
    /// </summary>
    private static string BuildMarkerInnerScript(string markerPath, string content) => $@"
$ErrorActionPreference = 'Stop'
Set-Content -Path '{markerPath}' -Value '{content}' -Encoding UTF8 -Force
exit 0
";

    private string MakeUniqueMarkerPath(string label)
    {
        // Pin into %TEMP% so cleanup doesn't accidentally touch ProgramData.
        var path = Path.Combine(Path.GetTempPath(),
            $"squid-upgrade-e2e-marker-{label}-{Guid.NewGuid():N}.txt");
        return path;
    }

    /// <summary>
    /// Runs the wrapper as a child PowerShell process. Captures stdout
    /// because the wrapper writes the registered task name there
    /// (<c>Write-Host "Task '<name>' registered ..."</c>) — we parse it back
    /// out for cleanup.
    /// </summary>
    private (int exitCode, string stdout) RunWrapper(string wrapperScript, TimeSpan timeout)
    {
        var tempScript = Path.Combine(Path.GetTempPath(), $"squid-upgrade-e2e-outer-{Guid.NewGuid():N}.ps1");
        // UTF-8 WITH BOM — Windows PowerShell 5.1 parses BOM-less UTF-8 as ANSI
        // codepage and mangles non-ASCII (em-dashes etc.) → parse error → exit 1.
        // Mirrors production LocalScriptService.WriteScriptFile's encoder choice.
        File.WriteAllText(tempScript, wrapperScript, new UTF8Encoding(true));
        _tempScriptsToCleanup.Add(tempScript);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempScript}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null)
            throw new InvalidOperationException("failed to launch powershell.exe");

        // Drain output streams concurrently to avoid pipe-buffer deadlock on
        // long log output.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw new TimeoutException(
                $"wrapper did not exit within {timeout}; this is itself a regression — the wrapper should be a fast scheduler-only script (typically <5s)");
        }

        var combinedStdout = stdoutTask.Result + Environment.NewLine + stderrTask.Result;
        return (proc.ExitCode, combinedStdout);
    }

    /// <summary>
    /// Parses the registered task name from the wrapper's stdout. Pinned
    /// pattern: <c>"[upgrade-wrapper] Task 'SquidTentacleUpgrade_&lt;32hex&gt;' registered"</c>.
    /// </summary>
    private static string ExtractTaskName(string wrapperStdout)
    {
        // Pattern intentionally tight (32-hex GUID without dashes) so a future
        // refactor that changes the GUID format would force this regex to
        // be updated too — the "extract task name" contract surfaces as a
        // test failure rather than a cleanup-leak in production.
        var match = Regex.Match(
            wrapperStdout,
            @"Task '(SquidTentacleUpgrade_[0-9a-f]{32})' registered",
            RegexOptions.CultureInvariant);

        if (!match.Success)
            throw new InvalidOperationException(
                $"wrapper stdout did not contain the 'Task '<name>' registered' marker line. " +
                $"Either the wrapper crashed before reaching that point, or the log format changed. " +
                $"Captured stdout:\n{wrapperStdout}");

        return match.Groups[1].Value;
    }

    /// <summary>
    /// %ProgramData%\Squid\Tentacle\upgrade\dispatch-&lt;TaskName&gt;.ps1 —
    /// contract dir + per-task dispatch script. The wrapper writes one file
    /// per task so concurrent dispatches don't race on a shared file. The
    /// caller passes the extracted task name (from wrapper stdout) so the
    /// computed path matches the file the wrapper actually wrote.
    /// </summary>
    private static string GetExpectedDispatchPath(string taskName)
    {
        var programData = Environment.GetEnvironmentVariable("ProgramData")
            ?? throw new InvalidOperationException("%ProgramData% must be set on Windows hosts");
        return Path.Combine(programData, "Squid", "Tentacle", "upgrade", $"dispatch-{taskName}.ps1");
    }

    private static bool WaitForFileExists(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path)) return true;
            Thread.Sleep(200);
        }
        return false;
    }

    /// <summary>
    /// Polls <c>schtasks /Query /TN &lt;name&gt;</c> until the task is gone
    /// (exit code != 0) or timeout. Inverse of WaitForFileExists.
    /// </summary>
    private static bool WaitForTaskGone(string taskName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!TaskExists(taskName)) return true;
            Thread.Sleep(500);
        }
        return false;
    }

    private static bool TaskExists(string taskName)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = $"/Query /TN \"{taskName}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) return false;
        proc.WaitForExit(5_000);
        return proc.ExitCode == 0;
    }

    private static void TryDeleteTask(string taskName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Delete /TN \"{taskName}\" /F",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5_000);
            // Idempotent: missing task returns non-zero; we don't care.
        }
        catch
        {
            // Cleanup is best-effort. A failing /Delete here is itself a test
            // bug (e.g. invalid task name extracted) but we don't want to
            // mask the real test failure.
        }
    }

    private static void TrySafeDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort.
        }
    }
}
