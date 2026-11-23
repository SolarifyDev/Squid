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
            echo "ActiveState=$(sed -n "${n}p" "$FAKE_DIR/states")"
            exit 0
          fi
        fi
        exit 0
        """;
}
