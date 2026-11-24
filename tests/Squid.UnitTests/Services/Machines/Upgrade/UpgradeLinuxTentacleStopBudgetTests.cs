using System.Diagnostics;
using System.IO;
using Squid.Core.Services.Machines.Upgrade;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// Pins the drain-aware restart/rollback helpers of <c>upgrade-linux-tentacle.sh</c>. The agent drains running
/// deployment scripts on stop (up to 300s, unit TimeoutStopSec=330), so the upgrade's systemctl waits must be
/// sized from the unit and a unit still `deactivating` must be waited out, never treated as stopped.
///
/// <para>The helper block is extracted from the production script and executed by real bash against a stub
/// <c>systemctl</c> (so the parsing, clamping and wait decisions are the shipped ones).</para>
/// </summary>
public sealed class UpgradeLinuxTentacleStopBudgetTests : IDisposable
{
    private const string BlockStart = "# ── Stop-budget helpers";
    private const string BlockEnd = "STOP_BUDGET_SECS=$(compute_stop_budget_secs)";

    private static readonly string RenderedScript = LinuxTentacleUpgradeStrategy.BuildScript("1.4.2");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"squid-stopbudget-{Guid.NewGuid():N}");

    public UpgradeLinuxTentacleStopBudgetTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "sudo"), "#!/bin/bash\nexec \"$@\"\n");
        File.WriteAllText(Path.Combine(_dir, "timeout"), "#!/bin/bash\nshift\nexec \"$@\"\n");
        File.WriteAllText(Path.Combine(_dir, "systemctl"), FakeSystemctl);

        foreach (var name in new[] { "sudo", "timeout", "systemctl" })
            File.SetUnixFileMode(Path.Combine(_dir, name), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Theory]
    [InlineData("5min 30s", "360")]
    [InlineData("1min 30s", "120")]
    [InlineData("infinity", "900")]
    [InlineData("", "60")]
    [InlineData("10s", "60")]
    [InlineData("500ms", "60")]
    [InlineData("2h", "900")]
    [InlineData("garbage", "60")]
    public void ComputeStopBudget_FromUnitTimeoutStop_AddsMarginAndClamps(string timeoutStopUsec, string expected)
    {
        var result = RunBlock("compute_stop_budget_secs", timeoutStopUsec, "inactive");

        result.Stdout.Trim().ShouldBe(expected);
    }

    [Fact]
    public void WaitUnitStopped_DeactivatingThenInactive_WaitsAndSucceeds()
    {
        var result = RunBlock("STOP_BUDGET_SECS=10; wait_unit_stopped && echo STOPPED || echo STILL_RUNNING", "5min 30s", "deactivating\ndeactivating\ninactive");

        result.Stdout.ShouldContain("STOPPED");
    }

    [Fact]
    public void WaitUnitStopped_StuckDeactivating_FailsAfterBudget()
    {
        var result = RunBlock("STOP_BUDGET_SECS=2; wait_unit_stopped && echo STOPPED || echo STILL_RUNNING", "5min 30s", "deactivating");

        result.Stdout.ShouldContain("STILL_RUNNING");
    }

    [Fact]
    public void StopUnitOrFailRollback_UnitStillDeactivating_ExitsCriticalWithoutReturning()
    {
        var result = RunBlock("STOP_BUDGET_SECS=2; stop_unit_or_fail_rollback; echo SAFE_TO_MOVE_DIRS", "5min 30s", "deactivating");

        result.ExitCode.ShouldBe(9);
        result.Stdout.ShouldContain("ROLLBACK_CRITICAL_FAILED");
        result.Stdout.ShouldNotContain("SAFE_TO_MOVE_DIRS");
    }

    [Fact]
    public void StopUnitOrFailRollback_DrainCompletes_ReturnsSoCallerMayProceed()
    {
        var result = RunBlock("STOP_BUDGET_SECS=10; stop_unit_or_fail_rollback; echo SAFE_TO_MOVE_DIRS", "5min 30s", "deactivating\ninactive");

        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldContain("SAFE_TO_MOVE_DIRS");
    }

    [Fact]
    public void WaitUnitStablyActive_SameProcessStaysActive_Succeeds()
    {
        var result = RunBlock("ROLLBACK_START_BUDGET_SECS=5; ROLLBACK_SETTLE_SECS=2; wait_unit_stably_active && echo STARTED || echo NOT_STARTED", "5min 30s", "activating 0\nactive 101");

        result.Stdout.ShouldContain("STARTED");
        result.Stdout.ShouldNotContain("NOT_STARTED");
    }

    [Fact]
    public void WaitUnitStablyActive_BrieflyActiveThenExited_Fails()
    {
        // A broken previous version under Type=simple: one `active` sample, then the process is gone.
        var result = RunBlock("ROLLBACK_START_BUDGET_SECS=4; ROLLBACK_SETTLE_SECS=2; wait_unit_stably_active && echo STARTED || echo NOT_STARTED", "5min 30s", "active 101\nfailed 0");

        result.Stdout.ShouldContain("NOT_STARTED");
    }

    [Fact]
    public void WaitUnitStablyActive_CrashLoopingUnderRestartOnFailure_Fails()
    {
        // Every sample is `active`, but systemd keeps restarting it with a new process: never the same PID twice.
        var result = RunBlock("ROLLBACK_START_BUDGET_SECS=4; ROLLBACK_SETTLE_SECS=2; wait_unit_stably_active && echo STARTED || echo NOT_STARTED", "5min 30s", "active 101\nactive 102\nactive 103\nactive 104\nactive 105\nactive 106\nactive 107");

        result.Stdout.ShouldContain("NOT_STARTED");
    }

    [Fact]
    public void Script_VersionedAndTarballRollback_AcceptOnlyAStablyActiveUnit()
    {
        CountOccurrences(RenderedScript, "if wait_unit_stably_active; then ROLLBACK_OK=1; fi").ShouldBe(2, "versioned + tarball rollback");
        RenderedScript.ShouldNotContain("ROLLBACK_OK=0\n  for i in $(seq 1 30); do\n    if sudo systemctl is-active --quiet", customMessage: "a single is-active poll accepts a previous version that starts and immediately crashes");
    }

    [Fact]
    public void Script_RestartAndRollbackWaits_AreSizedFromTheUnit_NotFixedConstants()
    {
        RenderedScript.ShouldContain("TimeoutStopUSec");
        RenderedScript.ShouldContain("timeout \"$RESTART_TIMEOUT_SECS\" sudo systemctl restart");
        RenderedScript.ShouldNotContain("timeout 90 sudo systemctl restart", customMessage: "a fixed 90s restart cap is shorter than the agent's 300s drain");
        RenderedScript.ShouldNotContain("timeout 30 sudo systemctl stop", customMessage: "a fixed 30s stop cap abandons a draining agent");
        RenderedScript.ShouldNotContain("timeout 30 sudo systemctl restart", customMessage: "package rollback restart must use the drain-aware cap too");
    }

    [Fact]
    public void Script_EveryRollbackBranch_WaitsForUnitStopBeforeTouchingInstallFiles()
    {
        CountOccurrences(RenderedScript, "\n  stop_unit_or_fail_rollback\n").ShouldBe(3, "versioned + tarball + apt rollback");
        CountOccurrences(RenderedScript, "\n    stop_unit_or_fail_rollback\n").ShouldBe(1, "yum rollback");
        CountOccurrences(RenderedScript, "\n  stop_unit_or_fail_rollback\n\n  ROLLBACK_INSTALL_OK").ShouldBe(1, "apt rollback");

        RenderedScript.ShouldContain("\n  stop_unit_or_fail_rollback\n\n  sudo ln -sfn \"$OLD_VER_TARGET\"", customMessage: "versioned: wait before repointing current");
        RenderedScript.ShouldContain("\n  stop_unit_or_fail_rollback\n  sudo rm -rf \"$INSTALL_DIR\"\n  sudo mv \"$BAK_DIR\"", customMessage: "tarball: wait before removing the live install dir");
        RenderedScript.ShouldContain("\n  stop_unit_or_fail_rollback\n\n  ROLLBACK_INSTALL_OK=0\n  if sudo dpkg -i --force-downgrade", customMessage: "apt: wait before dpkg replaces files");
        RenderedScript.ShouldContain("\n    stop_unit_or_fail_rollback\n\n    if sudo \"$YUM_BIN_FOR_ROLLBACK\" downgrade", customMessage: "yum: wait before dnf replaces files");
    }

    [Fact]
    public void TestService_StopsItsHealthzResponder_BeforeTheSimulatedDrain()
    {
        // The E2E harness waits only 30s for `systemctl stop`; a responder that survived the drain kept
        // answering 200 on the shared port and made the next test's failing healthz look healthy.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null && !File.Exists(Path.Combine(dir.FullName, ".git")) && !Directory.Exists(Path.Combine(dir.FullName, ".git"))) dir = dir.Parent;

        dir.ShouldNotBeNull();

        var script = File.ReadAllText(Path.Combine(dir.FullName, "tests", "Squid.LinuxTentacleE2ETests", "Infrastructure", "squid-linux-test-service.sh"));
        var cleanup = script.Substring(script.IndexOf("cleanup() {", StringComparison.Ordinal));
        var kill = cleanup.IndexOf("kill \"$HEALTHZ_PID\"", StringComparison.Ordinal);
        var drain = cleanup.IndexOf("sleep \"$SQUID_TEST_SERVICE_DRAIN_SECONDS\"", StringComparison.Ordinal);

        kill.ShouldBeGreaterThan(-1);
        drain.ShouldBeGreaterThan(kill);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private (int ExitCode, string Stdout) RunBlock(string body, string timeoutStopUsec, string states)
    {
        var start = RenderedScript.IndexOf(BlockStart, StringComparison.Ordinal);
        var end = RenderedScript.IndexOf(BlockEnd, StringComparison.Ordinal);

        start.ShouldBeGreaterThan(-1);
        end.ShouldBeGreaterThan(start);

        File.WriteAllText(Path.Combine(_dir, "states"), states + "\n");
        File.WriteAllText(Path.Combine(_dir, "counter"), "0");

        var script = "set -uo pipefail\nSERVICE_NAME=fake\nemit_event() { :; }\nwrite_status() { echo \"[status] $*\"; }\n"
                     + RenderedScript.Substring(start, end - start)
                     + body + "\n";
        var scriptPath = Path.Combine(_dir, "run.sh");
        File.WriteAllText(scriptPath, script);

        var psi = new ProcessStartInfo("bash", $"\"{scriptPath}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.Environment["PATH"] = _dir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        psi.Environment["FAKE_TIMEOUT_STOP"] = timeoutStopUsec;
        psi.Environment["FAKE_DIR"] = _dir;

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);

        return (process.ExitCode, stdout);
    }

    private const string FakeSystemctl = """
        #!/bin/bash
        # systemctl show -p <Property> <unit>
        if [ "$1" = "show" ]; then
          if [ "$3" = "TimeoutStopUSec" ]; then echo "TimeoutStopUSec=$FAKE_TIMEOUT_STOP"; exit 0; fi
          if [ "$3" = "ActiveState" ]; then
            n=$(cat "$FAKE_DIR/counter"); n=$((n + 1)); echo "$n" > "$FAKE_DIR/counter"
            total=$(wc -l < "$FAKE_DIR/states" | tr -d ' ')
            if [ "$n" -gt "$total" ]; then n=$total; fi
            echo "ActiveState=$(sed -n "${n}p" "$FAKE_DIR/states" | cut -d' ' -f1)"
            exit 0
          fi
          # MainPID belongs to the sample the last ActiveState read returned (optional second field: "active 101").
          if [ "$3" = "MainPID" ]; then
            n=$(cat "$FAKE_DIR/counter")
            total=$(wc -l < "$FAKE_DIR/states" | tr -d ' ')
            if [ "$n" -gt "$total" ]; then n=$total; fi
            echo "MainPID=$(sed -n "${n}p" "$FAKE_DIR/states" | cut -s -d' ' -f2)"
            exit 0
          fi
        fi
        exit 0
        """;
}
