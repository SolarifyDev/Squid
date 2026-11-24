using System.Diagnostics;
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

    [Fact]
    public void WaitForRestoredService_CrashingUnit_GivesUpAfterBoundedAttempts()
    {
        var result = RunPredicate("wait_for_restored_service", "OK", "1", "100", "0", hasCurl: true, curlOk: "0");

        result.ExitCode.ShouldNotBe(0, $"a unit that is active but never answers /healthz must not count as restored\nstderr:{result.Stderr}");
        result.Stdout.ShouldContain("is-active-calls=30", customMessage: "the wait must be bounded to the same 30 attempts the rollback loops always had");
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

    private static (int ExitCode, string Stdout, string Stderr) RunPredicate(string function, string baselineHealthz, string isActiveSeq, string mainPidSeq, string nRestartsSeq, bool hasCurl, string curlOk)
    {
        var bash = ResolveBash() ?? throw new InvalidOperationException("bash not found — cannot execute the upgrade script predicates");
        var stubDir = Directory.CreateTempSubdirectory("squid-rollback-health-").FullName;
        var harnessPath = Path.Combine(stubDir, "harness.sh");

        File.WriteAllText(harnessPath, BuildHarness(function, stubDir, baselineHealthz, isActiveSeq, mainPidSeq, nRestartsSeq, hasCurl, curlOk));

        try
        {
            return RunProcess(bash, harnessPath);
        }
        finally
        {
            Directory.Delete(stubDir, recursive: true);
        }
    }

    private static string BuildHarness(string function, string stubDir, string baselineHealthz, string isActiveSeq, string mainPidSeq, string nRestartsSeq, bool hasCurl, string curlOk)
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
            sleep() { :; }
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

    private static int CountOccurrences(string haystack, string needle) => Regex.Matches(haystack, Regex.Escape(needle)).Count;
}
