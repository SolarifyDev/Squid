using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Text.RegularExpressions;
using Squid.Core.Services.Machines.Upgrade;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// Executes the upgrade script's service-health predicates in real bash against
/// stubbed <c>sudo</c> / <c>systemctl</c> / <c>curl</c> / <c>sleep</c>.
///
/// <para>The predicates are the single proof every rollback path uses before it
/// writes ROLLED_BACK. A bare <c>systemctl is-active</c> probe is not proof:
/// for a <c>Type=simple</c> unit systemd reports <c>active</c> as soon as the
/// process is forked, so a restored binary that exits on startup reads as active
/// on the first probe — and the upgrade reported ROLLED_BACK (exit 4) over a
/// crashed agent instead of ROLLBACK_CRITICAL_FAILED (exit 9).</para>
/// </summary>
public sealed class UpgradeLinuxRollbackHealthTests
{
    private const string PredicatesStartMarker = "# ── Service health predicates";
    private const string PredicatesEndMarker = "# ── Restart service";

    private static readonly string RenderedScript = LinuxTentacleUpgradeStrategy.BuildScript("1.4.2");

    // ── Restored-service predicate (baseline OK → healthz, otherwise settle) ──

    [Theory]
    // Baseline answered /healthz → the restored version must answer it again.
    [InlineData("OK", "1", "100", "0", true, "1", true)]
    [InlineData("OK", "1", "100", "0", true, "0", false)]        // active (just forked) but /healthz down = crashing binary
    [InlineData("OK", "0", "100", "0", true, "1", false)]        // not active
    // Baseline OK is NOT enough on its own: answers /healthz once, then crashes inside the settle window.
    [InlineData("OK", "1 1 0", "100", "0", true, "1", false)]          // dies after answering healthz
    [InlineData("OK", "1", "100 100 200", "0", true, "1", false)]      // re-exec'd after answering healthz
    [InlineData("OK", "1", "100", "0 0 1", true, "1", false)]          // systemd restarted it after healthz
    // Baseline never answered /healthz (FAIL/unknown) or no curl → settle window.
    [InlineData("FAIL", "1", "100", "0", true, "0", true)]       // stays active, same PID, no restarts
    [InlineData("unknown", "1", "100", "0", false, "0", true)]   // no curl on host, stable unit
    [InlineData("FAIL", "1 0", "100", "0", true, "0", false)]    // active on first probe, dead after settle
    [InlineData("FAIL", "1", "100 200", "0", true, "0", false)]  // re-exec'd: MainPID changed across settle
    [InlineData("FAIL", "1", "100", "0 1", true, "0", false)]    // systemd restarted it during settle
    [InlineData("FAIL", "1", "0", "0", true, "0", false)]        // active with no main process
    [InlineData("FAIL", "0", "100", "0", true, "1", false)]      // not active at all
    public void RestoredServiceHealthy_RequiresHealthzOrSettledActiveUnit(string baselineHealthz, string isActiveSeq, string mainPidSeq, string nRestartsSeq, bool hasCurl, string curlOk, bool expectedHealthy)
    {
        var result = RunPredicate("restored_service_healthy", baselineHealthz, isActiveSeq, mainPidSeq, nRestartsSeq, hasCurl, curlOk);

        (result.ExitCode == 0).ShouldBe(expectedHealthy, $"baseline={baselineHealthz} is-active={isActiveSeq} MainPID={mainPidSeq} NRestarts={nRestartsSeq} curl={hasCurl}/{curlOk}\nstdout:{result.Stdout}\nstderr:{result.Stderr}");
    }

    [Theory]
    [InlineData("OK")]
    [InlineData("FAIL")]
    [InlineData("unknown")]
    public void SettleWindow_StableUnit_ObservesLongerThanOneRestartCycle(string baselineHealthz)
    {
        var result = RunPredicate("restored_service_healthy", baselineHealthz, "1", "100", "0", hasCurl: true, curlOk: "1");

        result.ExitCode.ShouldBe(0, result.Stderr);
        var slept = int.Parse(Regex.Match(result.Stdout, @"sleep-total=(\d+)").Groups[1].Value);

        // The production unit is RestartSec=10: a crash-restart cycle is >10s, so a window of 5s (the old
        // value) could never see a binary that crashes and waits to be restarted.
        slept.ShouldBeGreaterThan(ProductionRestartSec, $"settle window ({slept}s) must span at least one RestartSec={ProductionRestartSec} crash-restart cycle");
    }

    [Fact]
    public void SettleWindow_ProductionRestartSec_IsPinnedToTheUnitTheScriptSupervises()
    {
        // Drift detector: the window above is justified against SystemdServiceHost's RestartSec. Anchored to a
        // whole line of the unit text so the explanatory comment mentioning "RestartSec=10" cannot satisfy it.
        var unit = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Squid.Tentacle", "ServiceHost", "SystemdServiceHost.cs"));

        var match = Regex.Match(unit, @"^[ \t]*RestartSec=(\d+)[ \t]*\r?$", RegexOptions.Multiline);

        match.Success.ShouldBeTrue("the unit text SystemdServiceHost writes must contain a RestartSec= line of its own");
        int.Parse(match.Groups[1].Value).ShouldBe(ProductionRestartSec, "production RestartSec changed - re-derive SETTLE_WINDOW_SECS in upgrade-linux-tentacle.sh");
    }

    [Fact]
    public void SettleWindow_CrashLateInTheWindow_StillCaught()
    {
        // Crash-restart observed at second 14 (PID changes): the old 5s window never reaches it.
        var pids = string.Join(" ", Enumerable.Repeat("100", 14)) + " 200";

        var result = RunPredicate("service_stays_active", "FAIL", "1", pids, "0", hasCurl: true, curlOk: "1");

        result.ExitCode.ShouldNotBe(0, "a restart at second 14 of the settle window must fail the restored-service proof");
    }

    // ── Rollback start: survives systemd's start limit ──────────────────────

    [Theory]
    [InlineData("start", 30)]
    [InlineData("restart", 360)]
    public void RollbackStartUnit_StartLimitHit_ResetsFailedCounterThenStarts(string verb, int budget)
    {
        var result = RunStartLimitScenario($"rollback_start_unit {verb} {budget}", refuseUntilResetFailed: true);

        result.Stdout.ShouldContain("AFTER_START", customMessage: "a refused start must not kill the script under set -e");
        result.Stdout.ShouldContain("RESTORED");
        var calls = result.Stdout.Split('\n').Where(l => l.StartsWith("call:")).ToList();
        var reset = calls.FindIndex(c => c == "call:reset-failed squid-tentacle");
        var start = calls.FindIndex(c => c == $"call:{verb} squid-tentacle");

        reset.ShouldBeGreaterThanOrEqualTo(0, "reset-failed must be issued");
        start.ShouldBeGreaterThan(reset, "reset-failed must come BEFORE the start, otherwise start-limit-hit refuses it");
    }

    [Fact]
    public void RollbackStartUnit_StartRefusedAnyway_IsNonFatalAndHealthPredicateDecides()
    {
        var result = RunStartLimitScenario("rollback_start_unit start 30", refuseUntilResetFailed: false, neverStarts: true);

        result.Stdout.ShouldContain("AFTER_START", customMessage: "start failure is non-fatal; wait_for_restored_service is the judge");
        result.Stdout.ShouldContain("NOT_RESTORED");
    }

    [Fact]
    public void EveryRollbackPath_StartsThroughTheResetFailedHelper()
    {
        CountOccurrences(RenderedScript, "rollback_start_unit start 30").ShouldBe(2, "versioned + tarball");
        CountOccurrences(RenderedScript, "rollback_start_unit restart \"$RESTART_TIMEOUT_SECS\"").ShouldBe(2, "apt + yum");
        Regex.Matches(RenderedScript, @"timeout 30 sudo systemctl start").Count.ShouldBe(0, "no rollback path may start the unit without reset-failed");
        Regex.Matches(RenderedScript, @"^\s+timeout ""\$RESTART_TIMEOUT_SECS"" sudo systemctl restart", RegexOptions.Multiline).Count.ShouldBe(0);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(30)]
    [InlineData(90)]   // production default: a restored binary slower than 30s must still get the operator's window
    [InlineData(200)]
    public void WaitForRestoredService_CrashingUnit_GivesUpAfterConfiguredHealthcheckBudget(int retries)
    {
        var result = RunPredicate("wait_for_restored_service", "OK", "1", "100", "0", hasCurl: true, curlOk: "0", healthcheckRetries: retries);

        result.ExitCode.ShouldNotBe(0, $"a unit that is active but never answers /healthz must not count as restored\nstderr:{result.Stderr}");
        result.Stdout.ShouldContain($"is-active-calls={retries}", customMessage: "the restore wait must use the same HEALTHCHECK_RETRIES budget as the forward window");
    }

    [Fact]
    public void WaitForRestoredService_RestoredBinaryNeedingMoreThanThirtySeconds_StillSucceeds()
    {
        var result = RunPredicate("wait_for_restored_service", "OK", string.Join(" ", Enumerable.Repeat("0", 44)) + " 1", "100", "0", hasCurl: true, curlOk: "1", healthcheckRetries: 90);

        result.ExitCode.ShouldBe(0, $"becoming active on attempt 45 is inside the configured 90s budget\nstderr:{result.Stderr}");
    }

    // ── Forward check: healthz pass + settle ────────────────────────────────

    [Theory]
    [InlineData("1", "100", "0", true, true)]            // healthy and stable through the settle window
    [InlineData("1", "100", "0", false, false)]          // never answers /healthz within the window
    [InlineData("1 1 0", "100", "0", true, false)]       // answers healthz, dies during settle
    [InlineData("1", "100 100 200", "0", true, false)]   // answers healthz, re-exec'd (new PID) during settle
    [InlineData("1", "100", "0 0 1", true, false)]       // answers healthz, systemd restarted it during settle
    [InlineData("0 0 1", "100", "0", true, true)]        // slow start inside the window, then stable
    public void WaitForUpgradedService_RequiresHealthzThenSettle(string isActiveSeq, string mainPidSeq, string nRestartsSeq, bool curlOk, bool expectedHealthy)
    {
        var result = RunPredicate("wait_for_upgraded_service", "OK", isActiveSeq, mainPidSeq, nRestartsSeq, hasCurl: true, curlOk: curlOk ? "1" : "0", healthcheckRetries: 10);

        (result.ExitCode == 0).ShouldBe(expectedHealthy, $"is-active={isActiveSeq} MainPID={mainPidSeq} NRestarts={nRestartsSeq} curl={curlOk}\nstdout:{result.Stdout}\nstderr:{result.Stderr}");
        result.Stdout.ShouldContain(expectedHealthy ? "event:B healthz-pass" : "event:B healthz-fail");
    }

    [Fact]
    public void WaitForUpgradedService_HealthzThenCrashInsideSettle_IsReportedAsSettleFailure()
    {
        var result = RunPredicate("wait_for_upgraded_service", "OK", "1 1 0", "100", "0", hasCurl: true, curlOk: "1", healthcheckRetries: 10);

        result.ExitCode.ShouldNotBe(0);
        result.Stdout.ShouldContain("did not stay active", customMessage: "the failure event must say the settle failed, not that healthz never answered");
    }

    [Fact]
    public void ForwardPath_GatesHealthOnTheSettledCheck()
    {
        // Wiring seam: the restart section must take HEALTH_OK from the settle-aware check, never from a bare healthz loop.
        RenderedScript.ShouldContain("if wait_for_upgraded_service; then HEALTH_OK=1; fi");
        Regex.Matches(RenderedScript, @"seq 1 ""\$HEALTHCHECK_RETRIES""").Count.ShouldBe(2, "exactly the forward function and the restore wait consume the HEALTHCHECK_RETRIES budget");
    }

    [Fact]
    public void WaitForRestoredService_HealthyAfterSlowStart_Succeeds()
    {
        var result = RunPredicate("wait_for_restored_service", "OK", "0 0 0 1", "100", "0", hasCurl: true, curlOk: "1");

        result.ExitCode.ShouldBe(0, $"a restored unit that becomes active + healthy within the window must count as restored\nstderr:{result.Stderr}");
    }

    [Fact]
    public void ServiceHealthy_IsTheForwardCheck_ActiveAndHealthz()
    {
        RunPredicate("service_healthy", "FAIL", "1", "100", "0", hasCurl: true, curlOk: "1").ExitCode.ShouldBe(0);
        RunPredicate("service_healthy", "FAIL", "1", "100", "0", hasCurl: true, curlOk: "0").ExitCode.ShouldNotBe(0);
        RunPredicate("service_healthy", "FAIL", "1", "100", "0", hasCurl: false, curlOk: "1").ExitCode.ShouldNotBe(0);
    }

    // ── Wiring: every path goes through the shared predicates ────────────────

    [Fact]
    public void EveryRollbackPath_VerifiesThroughSharedPredicate()
    {
        // versioned + tarball + apt + yum — each must gate ROLLBACK_OK on the
        // shared predicate, never on a private is-active loop.
        CountOccurrences(RenderedScript, "if wait_for_restored_service; then ROLLBACK_OK=1; fi").ShouldBe(4,
            "each of the four rollback paths (versioned, tarball, apt, yum) must verify the restored service via wait_for_restored_service");
        Regex.Matches(RenderedScript, @"ROLLBACK_OK=1\s*\n\s*break").Count.ShouldBe(0,
            "no rollback path may keep its own polling loop — a private is-active loop is exactly how the tarball and versioned paths reported ROLLED_BACK over a crashing binary");
    }

    [Fact]
    public void ForwardHealthCheck_UsesSharedPredicate()
    {
        RenderedScript.ShouldContain("if service_healthy; then",
            customMessage: "the post-upgrade health check and the rollbacks must share one definition of 'healthy'");
    }

    [Fact]
    public void SettleWindow_ReadsUnitStateWithoutSudo()
    {
        // `systemctl show` is readable by any user — no new sudoers rule needed.
        var predicates = ExtractPredicates();

        predicates.ShouldContain("systemctl show -p MainPID --value \"$SERVICE_NAME\"");
        predicates.ShouldContain("systemctl show -p NRestarts --value \"$SERVICE_NAME\"");
        predicates.ShouldNotContain("sudo systemctl show");
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private static (int ExitCode, string Stdout, string Stderr) RunPredicate(string function, string baselineHealthz, string isActiveSeq, string mainPidSeq, string nRestartsSeq, bool hasCurl, string curlOk, int healthcheckRetries = 90)
    {
        var bash = ResolveBash() ?? throw new InvalidOperationException("bash not found — cannot execute the upgrade script predicates");
        var stubDir = Directory.CreateTempSubdirectory("squid-rollback-health-").FullName;
        var harnessPath = Path.Combine(stubDir, "harness.sh");

        File.WriteAllText(harnessPath, BuildHarness(function, stubDir, baselineHealthz, isActiveSeq, mainPidSeq, nRestartsSeq, hasCurl, curlOk, healthcheckRetries));

        try
        {
            return RunProcess(bash, harnessPath);
        }
        finally
        {
            Directory.Delete(stubDir, recursive: true);
        }
    }

    private static string BuildHarness(string function, string stubDir, string baselineHealthz, string isActiveSeq, string mainPidSeq, string nRestartsSeq, bool hasCurl, string curlOk, int healthcheckRetries)
    {
        // Without curl, hide the host's real binary from `command -v curl`.
        var curlStub = hasCurl
            ? $"curl() {{ [ \"{curlOk}\" = \"1\" ]; }}"
            : "command() { if [ \"$1\" = \"-v\" ] && [ \"$2\" = \"curl\" ]; then return 1; fi; builtin command \"$@\"; }";

        return $$"""
            set -euo pipefail
            STUB_DIR='{{stubDir}}'
            SERVICE_NAME=squid-tentacle
            HEALTHCHECK_URL=http://127.0.0.1:8080/healthz
            BASELINE_HEALTHZ_PRE='{{baselineHealthz}}'
            HEALTHCHECK_RETRIES={{healthcheckRetries}}
            emit_event() { echo "event:$1 $2 $3"; }

            # Each call consumes the next value of its sequence; the last value repeats.
            # File-backed so calls inside $(...) subshells advance the same cursor.
            next_value() {
              local key="$1" values="$2" n idx
              n=$(cat "$STUB_DIR/$key" 2>/dev/null || echo 0)
              echo $((n + 1)) > "$STUB_DIR/$key"
              set -- $values
              idx=$((n + 1)); if [ "$idx" -gt "$#" ]; then idx=$#; fi
              eval "echo \${$idx}"
            }
            sudo() { "$@"; }
            sleep() { echo "$1" >> "$STUB_DIR/sleeps"; }
            systemctl() {
              case "$1" in
                is-active) [ "$(next_value is-active '{{isActiveSeq}}')" = "1" ] ;;
                show) case "$3" in
                        MainPID) next_value mainpid '{{mainPidSeq}}' ;;
                        NRestarts) next_value nrestarts '{{nRestartsSeq}}' ;;
                      esac ;;
              esac
            }
            {{curlStub}}

            {{ExtractPredicates()}}

            rc=0
            {{function}} || rc=$?
            echo "is-active-calls=$(cat "$STUB_DIR/is-active" 2>/dev/null || echo 0)"
            echo "sleep-total=$(awk '{ s += $1 } END { print s + 0 }' "$STUB_DIR/sleeps" 2>/dev/null || echo 0)"
            exit $rc
            """;
    }

    private static string ExtractPredicates()
    {
        var start = RenderedScript.IndexOf(PredicatesStartMarker, StringComparison.Ordinal);
        var end = start < 0 ? -1 : RenderedScript.IndexOf(PredicatesEndMarker, start, StringComparison.Ordinal);

        if (start < 0 || end < 0)
            throw new InvalidOperationException($"Upgrade script must define its health predicates between '{PredicatesStartMarker}' and '{PredicatesEndMarker}'");

        return RenderedScript.Substring(start, end - start);
    }

    private static string ResolveBash()
    {
        foreach (var candidate in new[] { "/bin/bash", "/usr/bin/bash", "/usr/local/bin/bash", "/opt/homebrew/bin/bash" })
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static (int ExitCode, string Stdout, string Stderr) RunProcess(string file, string scriptPath)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo(file)
            {
                ArgumentList = { scriptPath },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        p.Start();
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(TimeSpan.FromSeconds(20));

        return (p.ExitCode, stdout, stderr);
    }

    private const int ProductionRestartSec = 10;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Squid.Tentacle"))) dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static (int ExitCode, string Stdout, string Stderr) RunStartLimitScenario(string startStep, bool refuseUntilResetFailed, bool neverStarts = false)
    {
        var bash = ResolveBash() ?? throw new InvalidOperationException("bash not found");
        var dir = Directory.CreateTempSubdirectory("squid-startlimit-").FullName;
        var harness = Path.Combine(dir, "harness.sh");

        File.WriteAllText(harness, $$"""
            set -euo pipefail
            STATE='{{dir}}'
            SERVICE_NAME=squid-tentacle
            HEALTHCHECK_URL=http://127.0.0.1:8080/healthz
            BASELINE_HEALTHZ_PRE=FAIL
            sudo() { "$@"; }
            timeout() { shift; "$@"; }
            sleep() { :; }
            # Stub of a unit that is in start-limit-hit: start/restart are refused until reset-failed.
            systemctl() {
              echo "call:$*"
              case "$1" in
                reset-failed) touch "$STATE/reset" ;;
                start|restart)
                  if [ '{{neverStarts}}' = 'True' ]; then return 1; fi
                  if [ '{{refuseUntilResetFailed}}' = 'True' ] && [ ! -f "$STATE/reset" ]; then return 1; fi
                  touch "$STATE/active" ;;
                is-active) [ -f "$STATE/active" ] ;;
                show) case "$3" in MainPID) echo 100 ;; NRestarts) echo 0 ;; esac ;;
              esac
            }

            {{ExtractPredicates()}}

            {{startStep}}
            echo AFTER_START
            if wait_for_restored_service; then echo RESTORED; else echo NOT_RESTORED; fi
            """);

        try
        {
            return RunProcess(bash, harness);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static int CountOccurrences(string haystack, string needle) => Regex.Matches(haystack, Regex.Escape(needle)).Count;
}
