using System.Text.RegularExpressions;
using Squid.Core.Services.Machines.Upgrade;
using SystemEnvironment = System.Environment;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// The upgrade-progress polling ceiling is each strategy's <see cref="IUpgradeDurationBudget.WorstCaseDuration"/>. These
/// tests recompute that worst case from the values in the RENDERED script (including the server-substituted
/// <c>HEALTHCHECK_RETRIES</c> / <c>SERVICE_TIMEOUT_SECONDS</c>, with operator overrides), so a script change that lengthens
/// any phase, or a renamed variable, fails here instead of silently ending the polling before the script's final event.
/// </summary>
[Collection(Squid.UnitTests.Support.GlobalStateSerialisedCollection.Name)]
public sealed class UpgradeScriptWorstCaseDriftTests : IDisposable
{
    private readonly string _linuxRetries = SystemEnvironment.GetEnvironmentVariable(LinuxTentacleUpgradeStrategy.HealthcheckRetriesEnvVar);
    private readonly string _windowsRetries = SystemEnvironment.GetEnvironmentVariable(WindowsTentacleUpgradeStrategy.HealthcheckRetriesEnvVar);
    private readonly string _windowsServiceTimeout = SystemEnvironment.GetEnvironmentVariable(WindowsTentacleUpgradeStrategy.ServiceTimeoutSecondsEnvVar);

    public void Dispose()
    {
        SystemEnvironment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.HealthcheckRetriesEnvVar, _linuxRetries);
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.HealthcheckRetriesEnvVar, _windowsRetries);
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.ServiceTimeoutSecondsEnvVar, _windowsServiceTimeout);
    }

    [Theory]
    [InlineData(null)]    // default HEALTHCHECK_RETRIES
    [InlineData("240")]   // operator override is rendered into the script AND must lengthen the ceiling
    public void Linux_WorstCaseDuration_MatchesTheRenderedScript_IncludingRollback(string retriesOverride)
    {
        SystemEnvironment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.HealthcheckRetriesEnvVar, retriesOverride);
        var script = LinuxTentacleUpgradeStrategy.BuildScript("1.4.2");

        var retries = Int(script, @"^HEALTHCHECK_RETRIES=(\d+)\s*$");
        var stopCeiling = Int(script, @"^STOP_BUDGET_CEILING_SECS=(\d+)\s*$");
        var startAllowance = Int(script, @"^RESTART_TIMEOUT_SECS=\$\(\(STOP_BUDGET_SECS \+ (\d+)\)\)");
        var settle = Int(script, @"^SETTLE_WINDOW_SECS=(\d+)\s*$");
        var probe = Int(FunctionBody(script, "service_healthy() {"), @"curl -fsS --max-time (\d+) ""\$HEALTHCHECK_URL""");
        var forwardSleep = Int(FunctionBody(script, "wait_for_upgraded_service() {"), @"^\s*sleep (\d+)\s*$");
        var restoredSleep = Int(FunctionBody(script, "wait_for_restored_service() {"), @"^\s*sleep (\d+)\s*$");

        var restart = stopCeiling + startAllowance;
        var forward = retries * (probe + forwardSleep) + settle;
        var rollback = stopCeiling + restart + retries * (probe + settle + restoredSleep);
        var expected = LinuxTentacleUpgradeStrategy.UpgradeScriptTimeout + TimeSpan.FromSeconds(restart + forward + rollback);

        Linux().WorstCaseDuration.ShouldBe(expected, "the polling ceiling must cover the rendered script's slowest path: drain-delayed restart, health window + settle, then a full rollback");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("90", "120")]
    public void Windows_WorstCaseDuration_MatchesTheRenderedScript_IncludingRollback(string retriesOverride, string serviceTimeoutOverride)
    {
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.HealthcheckRetriesEnvVar, retriesOverride);
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.ServiceTimeoutSecondsEnvVar, serviceTimeoutOverride);
        var script = WindowsTentacleUpgradeStrategy.RenderInnerScript("1.4.2", WindowsTentacleUpgradeStrategy.DefaultMethodOrder);

        var retries = Int(script, @"^\$HEALTHCHECK_RETRIES = (\d+)\s*$");
        var serviceTimeout = Int(script, @"^\$SERVICE_TIMEOUT_SECONDS = (\d+)\s*$");
        var scmSettle = Int(script, @"^\$SCM_RESTART_SETTLE_SECONDS = (\d+)\s*$");
        var healthLoop = Regex.Match(script, @"for \(\$i = 0; \$i -lt \$HEALTHCHECK_RETRIES; \$i\+\+\) \{\s*Start-Sleep -Seconds (\d+)[\s\S]*?-TimeoutSec (\d+)");

        healthLoop.Success.ShouldBeTrue("the healthcheck loop shape changed; update WindowsTentacleUpgradeStrategy.WorstCaseFor and this parser together");

        var settleStopStart = scmSettle + 2 * serviceTimeout;
        var forward = retries * (int.Parse(healthLoop.Groups[1].Value) + int.Parse(healthLoop.Groups[2].Value));
        var expected = WindowsTentacleUpgradeStrategy.UpgradeScriptTimeout + TimeSpan.FromSeconds(settleStopStart + forward + settleStopStart);

        Windows().WorstCaseDuration.ShouldBe(expected, "the polling ceiling must cover the rendered script's slowest path: settle + stop + start, the healthcheck window, then a full rollback");
    }

    [Fact]
    public void Windows_RollbackWaitsForTheScmSettleBeforeItsStop_SoTheWorstCaseCountsItTwice()
    {
        var script = WindowsTentacleUpgradeStrategy.RenderInnerScript("1.4.2", WindowsTentacleUpgradeStrategy.DefaultMethodOrder);

        FunctionBody(script, "function Invoke-Rollback {").ShouldContain("Wait-ScmPendingRestartToSettle");
        Regex.Matches(script, @"^\s*Wait-ScmPendingRestartToSettle\s*$", RegexOptions.Multiline).Count.ShouldBe(2, "forward stop + rollback stop; a third wait lengthens the worst case");
    }

    [Theory]
    [InlineData("linux")]
    [InlineData("windows")]
    public void ScriptStrategies_DeclareABudget_SoPollingOutlivesTheFixedDefaultWindow(string platform)
    {
        IMachineUpgradeStrategy strategy = platform == "linux" ? Linux() : Windows();

        UpgradeProgressPoller.CeilingFor(strategy).ShouldBe(((IUpgradeDurationBudget)strategy).WorstCaseDuration + UpgradeProgressPoller.ReconnectCushion);
        UpgradeProgressPoller.CeilingFor(strategy).ShouldBeGreaterThan(UpgradeProgressPoller.DefaultCeiling);
    }

    private static LinuxTentacleUpgradeStrategy Linux() => new(halibutClientFactory: null, observer: null);

    private static WindowsTentacleUpgradeStrategy Windows() => new(halibutClientFactory: null, observer: null);

    private static int Int(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.Multiline);

        match.Success.ShouldBeTrue($"pattern not found in the rendered script (renamed or reshaped?): {pattern}");

        return int.Parse(match.Groups[1].Value);
    }

    /// <summary>The text from <paramref name="header"/> to the first line that closes it (a lone <c>}</c> at column 0).</summary>
    private static string FunctionBody(string script, string header)
    {
        var start = script.IndexOf(header, StringComparison.Ordinal);

        start.ShouldBeGreaterThanOrEqualTo(0, $"function not found in the rendered script: {header}");

        var end = script.IndexOf("\n}", start, StringComparison.Ordinal);

        return script.Substring(start, end - start);
    }
}
