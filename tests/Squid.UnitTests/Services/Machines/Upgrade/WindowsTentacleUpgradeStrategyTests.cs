using System.Linq;
using Halibut;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Machines.Upgrade;
using Squid.Core.Services.Machines.Upgrade.Methods;
using Squid.Message.Commands.Machine;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Enums;
// Disambiguate: `using Squid.Core.Persistence.Entities.Deployments` brings in
// the entity `Environment` which collides with `System.Environment`. Alias
// the system one for env-var helpers.
using SystemEnvironment = System.Environment;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// coverage for the real Windows tentacle upgrade dispatch.
///
/// <para>Test strategy mirrors <see cref="LinuxTentacleUpgradeStrategyTests"/>:
/// pure functions (URL pattern, env override, embedded template + outer wrapper
/// shape) covered without infra; Halibut dispatch branches driven by mocked
/// <c>IHalibutClientFactory</c> + <c>IHalibutScriptObserver</c> for each of
/// the 5 outcome paths the real strategy emits (success / mid-disconnect /
/// pre-dispatch failure / liveness-probe abort / cancel / generic). The 16
/// stub-era CanHandle tests are preserved verbatim — routing
/// invariants don't change in 12.E.4.</para>
///
/// <para><b>Windows-specific divergence from Linux pin:</b>
/// <see cref="UpgradeAsync_ScriptSuccess_ReturnsInitiatedNotUpgraded"/> —
/// the Halibut-connected outer wrapper exits 0 after scheduling the detached
/// Task Scheduler task, NOT after the upgrade completes. So
/// <c>result.Success=true ExitCode=0</c> maps to
/// <see cref="MachineUpgradeStatus.Initiated"/>, NOT
/// <see cref="MachineUpgradeStatus.Upgraded"/>. The actual outcome arrives
/// via the next health check reading <c>last-upgrade.json</c>.</para>
/// </summary>
[Collection(Squid.UnitTests.Support.GlobalStateSerialisedCollection.Name)]
public sealed class WindowsTentacleUpgradeStrategyTests : IDisposable
{
    private readonly string _previousBaseUrlOverride;
    private readonly string _previousHealthcheckUrlOverride;
    private readonly string _previousHealthcheckRetriesOverride;
    private readonly string _previousHealthcheckFatalOverride;
    private readonly string _previousServiceTimeoutOverride;

    public WindowsTentacleUpgradeStrategyTests()
    {
        _previousBaseUrlOverride = SystemEnvironment.GetEnvironmentVariable(WindowsTentacleUpgradeStrategy.DownloadBaseUrlEnvVar);
        _previousHealthcheckUrlOverride = SystemEnvironment.GetEnvironmentVariable(WindowsTentacleUpgradeStrategy.HealthcheckUrlEnvVar);
        _previousHealthcheckRetriesOverride = SystemEnvironment.GetEnvironmentVariable(WindowsTentacleUpgradeStrategy.HealthcheckRetriesEnvVar);
        _previousHealthcheckFatalOverride = SystemEnvironment.GetEnvironmentVariable(WindowsTentacleUpgradeStrategy.HealthcheckFatalEnvVar);
        _previousServiceTimeoutOverride = SystemEnvironment.GetEnvironmentVariable(WindowsTentacleUpgradeStrategy.ServiceTimeoutSecondsEnvVar);

        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.DownloadBaseUrlEnvVar, null);
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.HealthcheckUrlEnvVar, null);
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.HealthcheckRetriesEnvVar, null);
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.HealthcheckFatalEnvVar, null);
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.ServiceTimeoutSecondsEnvVar, null);
    }

    public void Dispose()
    {
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.DownloadBaseUrlEnvVar, _previousBaseUrlOverride);
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.HealthcheckUrlEnvVar, _previousHealthcheckUrlOverride);
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.HealthcheckRetriesEnvVar, _previousHealthcheckRetriesOverride);
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.HealthcheckFatalEnvVar, _previousHealthcheckFatalOverride);
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.ServiceTimeoutSecondsEnvVar, _previousServiceTimeoutOverride);
    }

    // ========================================================================
    // CanHandle: (style, OS) tuple routing — preserved from stub.
    // ========================================================================

    [Theory]
    [InlineData("TentaclePolling", true)]
    [InlineData("TentacleListening", true)]
    [InlineData("KubernetesAgent", false)]
    [InlineData("KubernetesApi", false)]
    [InlineData("Ssh", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CanHandle_OnlyMatchesTentacleStyles_OnWindows(string style, bool expected)
    {
        var strategy = new WindowsTentacleUpgradeStrategy(halibutClientFactory: null, observer: null);
        var windowsCaps = new MachineRuntimeCapabilities { Os = "Windows" };

        strategy.CanHandle(style, windowsCaps).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Windows", true)]
    [InlineData("windows", true)]
    [InlineData("WINDOWS", true)]
    // Legacy long-form Windows strings — the operator's specific failure mode
    // (TentaclePolling + Windows agent reporting "Microsoft Windows NT
    // 10.0.19044.0" via Environment.OSVersion.VersionString). Without these
    // claims, BOTH Windows AND Linux strategies reject → ResolveStrategy
    // returns null → UI shows "is not supported for in-UI upgrades" even
    // though the agent IS Windows. The fix in MachineRuntimeCapabilities.IsWindows
    // (delegate to WindowsOsStringHelper) is what makes these pass.
    [InlineData("Microsoft Windows NT 10.0.19045.0", true)]    // Win10 22H2 (operator's scenario)
    [InlineData("Microsoft Windows NT 10.0.22631.0", true)]    // Win11 23H2
    [InlineData("Microsoft Windows Server 2022 Datacenter", true)]
    [InlineData("Linux", false)]
    [InlineData("macOS", false)]
    [InlineData("", false)]                // cold cache → falls to Linux historical default, NOT Windows
    [InlineData("Unknown", false)]         // agent's "Unknown" fallback also falls to Linux, never Windows
    [InlineData("FreeBSD", false)]         // any unrecognised OS → no Windows claim
    public void CanHandle_RoutesByOs_OnlyClaimsWindowsAgents(string os, bool expected)
    {
        var strategy = new WindowsTentacleUpgradeStrategy(halibutClientFactory: null, observer: null);
        var capabilities = new MachineRuntimeCapabilities { Os = os };

        strategy.CanHandle(nameof(CommunicationStyle.TentaclePolling), capabilities).ShouldBe(expected);
    }

    [Fact]
    public void CanHandle_NullCapabilities_TreatedAsEmpty_DoesNotClaimWindows()
    {
        var strategy = new WindowsTentacleUpgradeStrategy(halibutClientFactory: null, observer: null);

        strategy.CanHandle(nameof(CommunicationStyle.TentaclePolling), capabilities: null).ShouldBeFalse();
    }

    // ========================================================================
    // Constants pinning (Rule 8 — operator-facing env vars MUST be hard-pinned
    // by test or a rename silently breaks every air-gapped operator who set
    // the previous name).
    // ========================================================================

    [Fact]
    public void DownloadBaseUrlEnvVar_ConstantNamePinned()
    {
        WindowsTentacleUpgradeStrategy.DownloadBaseUrlEnvVar
            .ShouldBe("SQUID_TARGET_WINDOWS_TENTACLE_DOWNLOAD_BASE_URL");
    }

    [Fact]
    public void HealthcheckUrlEnvVar_ConstantNamePinned()
    {
        WindowsTentacleUpgradeStrategy.HealthcheckUrlEnvVar
            .ShouldBe("SQUID_TARGET_WINDOWS_TENTACLE_HEALTHCHECK_URL");
    }

    [Fact]
    public void HealthcheckRetriesEnvVar_ConstantNamePinned()
    {
        // Renaming this constant breaks every air-gapped operator who pinned
        // a slow-startup retry count via env. Hard-pin the literal.
        WindowsTentacleUpgradeStrategy.HealthcheckRetriesEnvVar
            .ShouldBe("SQUID_TARGET_WINDOWS_TENTACLE_HEALTHCHECK_RETRIES");
    }

    [Fact]
    public void ResolveHealthcheckRetries_NoEnvVar_ReturnsDefault30()
    {
        // Default 30 attempts × 2s = 60s wait window. Pin the default so a
        // future polish that "tightens" or "loosens" the wait surfaces in
        // review (operator-impacting change → must be intentional).
        WindowsTentacleUpgradeStrategy.ResolveHealthcheckRetries().ShouldBe(30);
    }

    [Theory]
    [InlineData("1", 1)]                 // tests use this to bypass the wait
    [InlineData("90", 90)]               // air-gap operator with slow-starting plugin host
    [InlineData("  45  ", 45)]           // whitespace tolerant
    [InlineData("3600", 3600)]           // 2h wait — extreme but allowed
    public void ResolveHealthcheckRetries_ValidValue_RoundTripsAsInteger(string envValue, int expected)
    {
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.HealthcheckRetriesEnvVar, envValue);

        WindowsTentacleUpgradeStrategy.ResolveHealthcheckRetries().ShouldBe(expected);
    }

    [Theory]
    [InlineData("0")]                    // 0 is invalid (would skip the wait entirely on a logical level — not what operators mean)
    [InlineData("-1")]                   // negative is invalid
    [InlineData("not-a-number")]         // typo
    [InlineData("3.14")]                 // non-integer
    [InlineData("inf")]                  // float-special
    public void ResolveHealthcheckRetries_InvalidValue_FallsBackToDefault(string envValue)
    {
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.HealthcheckRetriesEnvVar, envValue);

        // Operator-friendly: a typo'd env var must NOT break upgrades; falls
        // back to the safe 30-attempt default with a Serilog warning so
        // operators see the typo on their next dispatch.
        WindowsTentacleUpgradeStrategy.ResolveHealthcheckRetries().ShouldBe(30);
    }

    // ── J.E.8: ServiceTimeoutSecondsEnvVar pins ────────────────────────────

    [Fact]
    public void ServiceTimeoutSecondsEnvVar_ConstantNamePinned()
    {
        // Renaming this constant breaks operators who tuned `WaitForStatus`
        // upper bounds for slow-startup agents. Hard-pin the literal.
        WindowsTentacleUpgradeStrategy.ServiceTimeoutSecondsEnvVar
            .ShouldBe("SQUID_TARGET_WINDOWS_TENTACLE_SERVICE_TIMEOUT_SECONDS");
    }

    [Fact]
    public void ResolveServiceTimeoutSeconds_NoEnvVar_ReturnsDefault30()
    {
        // 30s default = stock-agent boot (3-5s) × 6 margin. Pin the
        // default — a polish that "tightens" or "loosens" without operator
        // notice would mass-break heavyweight-agent deployments.
        WindowsTentacleUpgradeStrategy.ResolveServiceTimeoutSeconds().ShouldBe(30);
    }

    [Theory]
    [InlineData("60", 60)]                // typical "moderate slow agent" (1 min)
    [InlineData("180", 180)]              // very slow agent (3 min, plugin enumeration heavy)
    [InlineData("3600", 3600)]            // 1h — extreme but allowed (e.g., AV scanning huge bundle)
    [InlineData("  120  ", 120)]          // whitespace tolerant
    public void ResolveServiceTimeoutSeconds_ValidValue_RoundTripsAsInteger(string envValue, int expected)
    {
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.ServiceTimeoutSecondsEnvVar, envValue);

        WindowsTentacleUpgradeStrategy.ResolveServiceTimeoutSeconds().ShouldBe(expected);
    }

    [Theory]
    [InlineData("0")]                     // 0 is invalid (would skip the wait → race conditions)
    [InlineData("-30")]                   // negative is invalid
    [InlineData("30s")]                   // operator wrote "30s" instead of "30" — surprisingly common
    [InlineData("not-a-number")]
    [InlineData("")]
    public void ResolveServiceTimeoutSeconds_InvalidValue_FallsBackToDefault30(string envValue)
    {
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.ServiceTimeoutSecondsEnvVar, envValue);

        // Operator-friendly: invalid values fall back to 30s + Serilog warning.
        // A typo'd env var doesn't accidentally pass 0 to WaitForStatus
        // (which would race mid-Stop and cause Phase B to crash mid-swap).
        WindowsTentacleUpgradeStrategy.ResolveServiceTimeoutSeconds().ShouldBe(30);
    }

    [Fact]
    public void RenderInnerScript_ServiceTimeoutSecondsPlaceholder_SubstitutedFromEnv()
    {
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.ServiceTimeoutSecondsEnvVar, "120");

        var inner = WindowsTentacleUpgradeStrategy.RenderInnerScript("1.6.0", WindowsTentacleUpgradeStrategy.DefaultMethodOrder);

        // Direct substitution: the placeholder gets the resolved integer.
        inner.ShouldContain("$SERVICE_TIMEOUT_SECONDS = 120",
            customMessage: "RenderInnerScript MUST substitute the env-resolved timeout into the script. " +
                          "If this fails: the placeholder wasn't wired through Replace, OR the .ps1 dropped the variable assignment line.");

        // Hardcoded '00:00:30' literals MUST be gone from the rendered script
        // — every WaitForStatus call now uses $SERVICE_TIMEOUT_SPAN.
        inner.ShouldNotContain("'00:00:30'",
            customMessage: "stale '00:00:30' literal in the rendered .ps1 — should reference $SERVICE_TIMEOUT_SPAN dynamically. " +
                          "If present: a WaitForStatus call still uses the hardcoded 30s upper bound, ignoring the operator's env var. " +
                          "Heavy-agent operators would see false-rollback timeouts despite setting the override.");
    }

    [Fact]
    public void RenderInnerScript_ServiceTimeoutSpanIsTimeSpanInstance()
    {
        // Pin the .ps1's variable shape — `$SERVICE_TIMEOUT_SPAN =
        // [TimeSpan]::FromSeconds($SERVICE_TIMEOUT_SECONDS)` — so a future
        // polish that "simplifies" by passing the integer directly to
        // WaitForStatus (which expects TimeSpan) doesn't break Stop/Start
        // synchronisation across the .ps1.
        var inner = WindowsTentacleUpgradeStrategy.RenderInnerScript("1.6.0", WindowsTentacleUpgradeStrategy.DefaultMethodOrder);

        inner.ShouldContain("[TimeSpan]::FromSeconds($SERVICE_TIMEOUT_SECONDS)",
            customMessage: "TimeSpan construction REGRESSED — WaitForStatus expects TimeSpan, passing int causes runtime cast errors");
        inner.ShouldContain("WaitForStatus('Running', $SERVICE_TIMEOUT_SPAN)",
            customMessage: "Start-Service WaitForStatus REGRESSED — must use $SERVICE_TIMEOUT_SPAN");
        inner.ShouldContain("WaitForStatus('Stopped', $SERVICE_TIMEOUT_SPAN)",
            customMessage: "Stop-Service WaitForStatus REGRESSED — must use $SERVICE_TIMEOUT_SPAN");
    }

    [Fact]
    public void RenderInnerScript_DownloadUrl_RewritesLiteralRidTokenBeforeDownload()
    {
        // PRODUCTION BUG GUARD (Windows upgrade "Download failed: (404)"):
        // The server emits the download URL with a LITERAL '$RID' token
        // ($DOWNLOAD_URL = '.../squid-tentacle-<ver>-$RID.zip'). PowerShell single-quoted
        // assignment does NOT expand $RID, and PowerShell never re-expands a variable's
        // stored value at use time — so the script MUST explicitly rewrite the '$RID' token
        // to the architecture-resolved $RID BEFORE WebClient.DownloadFile consumes $DOWNLOAD_URL.
        // Without it the agent requests .../squid-tentacle-<ver>-$RID.zip → GitHub 404.
        var inner = WindowsTentacleUpgradeStrategy.RenderInnerScript("1.6.0", WindowsTentacleUpgradeStrategy.DefaultMethodOrder);

        var literalUrlIdx = inner.IndexOf("-$RID.zip'", StringComparison.Ordinal);
        var rewriteIdx = inner.IndexOf(".Replace('$RID', $RID)", StringComparison.Ordinal);
        var downloadIdx = inner.IndexOf("DownloadFile($DOWNLOAD_URL", StringComparison.Ordinal);

        literalUrlIdx.ShouldBeGreaterThan(-1,
            customMessage: "Expected the server to emit the single-quoted URL with a literal $RID token.");

        rewriteIdx.ShouldBeGreaterThan(literalUrlIdx,
            customMessage: "Download URL must rewrite the literal '$RID' token to the resolved $RID. " +
                           "PowerShell will NOT expand $RID inside a single-quoted value, so without " +
                           "$DOWNLOAD_URL = $DOWNLOAD_URL.Replace('$RID', $RID) the agent downloads " +
                           ".../squid-tentacle-<ver>-$RID.zip and GitHub returns 404.");

        downloadIdx.ShouldBeGreaterThan(rewriteIdx,
            customMessage: "The $RID rewrite MUST happen before WebClient.DownloadFile consumes $DOWNLOAD_URL.");
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    public void RenderInnerScript_AgentResolvedDownloadUrl_EqualsBuilderForEachArch(string rid)
    {
        // Closes the gap that let the 404 ship: BuildDownloadUrl_DefaultsToGitHubReleasesZipPath
        // passed a CONCRETE rid and proved the builder — but the real dispatch path embeds
        // BuildDownloadUrl(version,"{RID}") → "$RID" into a single-quoted line and defers
        // resolution to the agent, which the isolated test never exercised. Reproduce the
        // agent's resolution from the rendered script and pin it to the canonical builder URL
        // for EVERY supported arch. Fails if the rewrite is missing (URL keeps $RID ≠ builder)
        // or if the embedded URL/extension/version ever diverges from the builder.
        const string version = "1.6.0";
        var inner = WindowsTentacleUpgradeStrategy.RenderInnerScript(version, WindowsTentacleUpgradeStrategy.DefaultMethodOrder);

        System.Text.RegularExpressions.Regex.IsMatch(inner, @"\$DOWNLOAD_URL\s*=\s*\$DOWNLOAD_URL\.Replace\('\$RID',\s*\$RID\)")
            .ShouldBeTrue(customMessage: "the script must rewrite the literal $RID token — a single-quoted assignment never expands it.");

        var embeddedLiteral = ExtractDownloadUrlLiteral(inner);
        var agentResolved = embeddedLiteral.Replace("$RID", rid, StringComparison.Ordinal);

        agentResolved.ShouldBe(WindowsTentacleUpgradeStrategy.BuildDownloadUrl(version, rid),
            customMessage: $"agent-resolved download URL for {rid} diverged from BuildDownloadUrl — " +
                           "the dispatched script and the builder must produce the same URL.");
        agentResolved.ShouldNotContain("$RID", customMessage: "resolved URL still carries a literal $RID token.");
        agentResolved.ShouldNotContain("{RID}", customMessage: "resolved URL still carries a {RID} placeholder.");
    }

    [Fact]
    public void RenderInnerScript_NoUnsubstitutedServerPlaceholdersRemain()
    {
        // Any {{PLACEHOLDER}} left in the dispatched script is a substitution the strategy forgot
        // to fill; it reaches the agent verbatim and breaks at runtime (the $RID bug's cousin).
        // One invariant guards the entire server-side placeholder surface, not just the ones with
        // bespoke tests. The token pattern is [A-Z0-9_]+ (matches {{EXPECTED_SHA256}} and friends)
        // — deliberately NOT a broad {{...}} match, which would false-positive on the script
        // header's own `{{...}}` documentation comment.
        var inner = WindowsTentacleUpgradeStrategy.RenderInnerScript("1.6.0", WindowsTentacleUpgradeStrategy.DefaultMethodOrder);

        System.Text.RegularExpressions.Regex.Matches(inner, @"\{\{[A-Z0-9_]+\}\}").Count
            .ShouldBe(0, customMessage: "an unsubstituted {{PLACEHOLDER}} survived into the dispatched script.");
    }

    // The URL the server embedded in `$DOWNLOAD_URL = '<url>'` (single-quoted → PowerShell stores
    // it verbatim). The rewrite line below it carries no single-quoted URL, so the first match is
    // the assignment.
    private static string ExtractDownloadUrlLiteral(string renderedScript)
    {
        var match = System.Text.RegularExpressions.Regex.Match(renderedScript, @"\$DOWNLOAD_URL\s*=\s*'([^']*)'");

        match.Success.ShouldBeTrue(customMessage: "could not find the $DOWNLOAD_URL single-quoted assignment in the rendered script.");

        return match.Groups[1].Value;
    }

    // ── J.E.7: HealthcheckFatalEnvVar pins ──────────────────────────────────

    [Fact]
    public void HealthcheckFatalEnvVar_ConstantNamePinned()
    {
        // Renaming this constant breaks every operator who pinned strict-mode
        // healthcheck behaviour via env. Hard-pin the literal.
        WindowsTentacleUpgradeStrategy.HealthcheckFatalEnvVar
            .ShouldBe("SQUID_TARGET_WINDOWS_TENTACLE_HEALTHCHECK_FATAL");
    }

    [Fact]
    public void ResolveHealthcheckFatal_NoEnvVar_ReturnsFalse()
    {
        // Default (env unset) MUST be false — matches Octopus Tentacle's
        // permissive default. A polish that flips the default to true would
        // mean every existing deployment with a slow-starting agent suddenly
        // starts auto-rolling-back after upgrade.
        WindowsTentacleUpgradeStrategy.ResolveHealthcheckFatal().ShouldBeFalse();
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData("  yes  ", true)]   // whitespace tolerant
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("off", false)]
    public void ResolveHealthcheckFatal_RecognisedValue_ParsesCorrectly(string envValue, bool expected)
    {
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.HealthcheckFatalEnvVar, envValue);

        WindowsTentacleUpgradeStrategy.ResolveHealthcheckFatal().ShouldBe(expected);
    }

    [Theory]
    [InlineData("strict")]               // operator might think this is the strict-mode flag value
    [InlineData("enabled")]              // operator-friendly synonym we don't recognise
    [InlineData("YES_PLEASE")]           // typo with an obvious intent
    [InlineData("fatal")]                // ironic — looks like the variable but isn't recognised
    [InlineData("")]                     // empty-after-trim is treated as unset above
    public void ResolveHealthcheckFatal_UnrecognisedValue_FallsBackToFalseWithWarning(string envValue)
    {
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.HealthcheckFatalEnvVar, envValue);

        // Operator-friendly: a typo doesn't accidentally enable strict mode
        // (which would surprise them with auto-rollbacks). Falls back to
        // permissive default; warning surfaces the typo in logs.
        WindowsTentacleUpgradeStrategy.ResolveHealthcheckFatal().ShouldBeFalse();
    }

    [Fact]
    public void RenderInnerScript_HealthcheckFatalPlaceholder_SubstitutedAsPowerShellBoolean()
    {
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.HealthcheckFatalEnvVar, "true");

        var inner = WindowsTentacleUpgradeStrategy.RenderInnerScript("1.6.0", WindowsTentacleUpgradeStrategy.DefaultMethodOrder);

        // Strategy substitutes `$true` / `$false` (PowerShell boolean
        // literals — no quotes) so the .ps1's `$HEALTHCHECK_FATAL = ...`
        // assignment is type-safe regardless of operator's env var format.
        inner.ShouldContain("$HEALTHCHECK_FATAL = $true",
            customMessage: "RenderInnerScript MUST substitute $true (PowerShell boolean literal) when env-resolved fatal mode is on. " +
                          "Quoted form ($HEALTHCHECK_FATAL = 'true') would make the variable a non-empty string, which is truthy in `if ($HEALTHCHECK_FATAL)` checks " +
                          "→ FATAL=false would BEHAVE like FATAL=true. Safety regression. Pin the literal substitution shape.");

        // The healthcheck loop MUST consult the variable.
        inner.ShouldContain("if ($HEALTHCHECK_FATAL)",
            customMessage: "healthcheck-fatal branch REGRESSED — operator opt-in is no-op without the if-check.");
    }

    [Fact]
    public void RenderInnerScript_HealthcheckFatal_DefaultsToFalseLiteralInRender()
    {
        // No env var set → default false → strategy substitutes $false literal.
        var inner = WindowsTentacleUpgradeStrategy.RenderInnerScript("1.6.0", WindowsTentacleUpgradeStrategy.DefaultMethodOrder);

        inner.ShouldContain("$HEALTHCHECK_FATAL = $false",
            customMessage: "default render MUST inject $false — operators not-using-strict-mode see no behaviour change");
        inner.ShouldNotContain("$HEALTHCHECK_FATAL = $true",
            customMessage: "default render leaked strict-mode literal — every dispatch would trigger rollback on healthcheck timeout");
    }

    // ── J.E.7: stale-lock structural pin ───────────────────────────────────

    [Fact]
    public void RenderInnerScript_StaleLockBreak_PinnedStructurally()
    {
        // Pre-J.E.7 a crashed dispatch (host reboot mid-upgrade, OOM kill,
        // etc.) left the lock file with a dead PID. Every subsequent
        // upgrade dispatch failed with exit 13 forever until manual lock-
        // file deletion. Pin the recovery contract:
        //   1. Read the existing PID from the lock
        //   2. Probe via Get-Process to determine alive vs dead
        //   3. Live PID → keep exit 13 (real concurrent dispatch rejected)
        //   4. Dead PID → log warning + Remove-Item + fall through
        var asm = typeof(WindowsTentacleUpgradeStrategy).Assembly;
        using var stream = asm.GetManifestResourceStream("Squid.Core.Resources.Upgrade.upgrade-windows-tentacle.ps1")
            ?? throw new System.IO.InvalidDataException("embedded template not found");
        using var reader = new System.IO.StreamReader(stream);
        var template = reader.ReadToEnd();

        // The Get-Process probe is the heart of stale detection.
        template.ShouldContain("Get-Process -Id $existingPid",
            customMessage: "stale-lock detection REGRESSED — without Get-Process probe, every previously-crashed dispatch leaves the lock orphaned forever");

        // Stale-detected branch: warning + Remove-Item.
        template.ShouldContain("breaking lock to recover",
            customMessage: "stale-lock recovery log message REGRESSED — operators looking for 'lock broken' in upgrade.log won't find it");
        template.ShouldContain("Remove-Item -Path $LOCK_FILE",
            customMessage: "stale-lock break REGRESSED — Get-Process detected dead PID but didn't actually delete the orphan file");

        // Live-PID branch still exits 13 (concurrent-dispatch protection intact).
        template.ShouldContain("held by LIVE PID",
            customMessage: "live-PID detection REGRESSED — concurrent real dispatches no longer differentiated from stale ones in logs");
    }

    [Fact]
    public void RenderInnerScript_HealthcheckRetriesPlaceholder_SubstitutedFromEnv()
    {
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.HealthcheckRetriesEnvVar, "5");

        var inner = WindowsTentacleUpgradeStrategy.RenderInnerScript("1.6.0", WindowsTentacleUpgradeStrategy.DefaultMethodOrder);

        // Direct substitution: the placeholder gets the resolved value as
        // a numeric literal (no quotes / no [int] cast — see .ps1 comment).
        inner.ShouldContain("$HEALTHCHECK_RETRIES = 5",
            customMessage: "RenderInnerScript MUST substitute the env-resolved retry count into the script. " +
                          "If this fails: the placeholder wasn't wired through Replace, OR the .ps1 dropped the variable assignment line.");

        // The previously-hardcoded 60s wait message should NOT remain in
        // the inner — the new dynamic wait message uses the variable.
        inner.ShouldNotContain("within 60s",
            customMessage: "stale '60s' literal in the .ps1 — should reference $HEALTHCHECK_RETRIES * 2 dynamically so air-gap operators with retries=90 see the correct wait window");
    }

    [Fact]
    public void DefaultInstallDir_PinnedToCanonicalProgramFilesPath()
    {
        // Operators who installed via 's install-tentacle.ps1 land
        // in this exact path. Drift here breaks every upgrade against a
        // canonically-installed agent (Phase B's Stop-Service / Move-Item
        // would target the wrong dir).
        WindowsTentacleUpgradeStrategy.DefaultInstallDir.ShouldBe(@"C:\Program Files\Squid Tentacle");
    }

    [Fact]
    public void DefaultServiceName_PinnedToSquidTentacle()
    {
        // sc.exe service name from 's WindowsServiceHost — operators'
        // runbooks (`sc query squid-tentacle`, `Get-Service squid-tentacle`)
        // depend on this literal.
        WindowsTentacleUpgradeStrategy.DefaultServiceName.ShouldBe("squid-tentacle");
    }

    [Fact]
    public void UpgradeScriptTimeout_StrictlyLessThanLockExpiry()
    {
        // Cross-strategy invariant: the orchestrator's Redis lock must outlive
        // the strategy's worst-case dispatch budget so an abandoned-but-still-
        // running strategy can't have its lock TTL'd out from under it.
        // Mirrors LinuxTentacleUpgradeStrategy's same invariant.
        WindowsTentacleUpgradeStrategy.UpgradeScriptTimeout
            .ShouldBeLessThan(MachineUpgradeService.LockExpiry);
    }

    // ========================================================================
    // Download URL builder — Windows zip URL pattern (vs Linux tarball).
    // ========================================================================

    [Theory]
    [InlineData("1.6.0", "win-x64", "https://github.com/SolarifyDev/Squid/releases/download/1.6.0/squid-tentacle-1.6.0-win-x64.zip")]
    [InlineData("1.6.0", "win-arm64", "https://github.com/SolarifyDev/Squid/releases/download/1.6.0/squid-tentacle-1.6.0-win-arm64.zip")]
    [InlineData("2.0.0-beta.1", "win-x64", "https://github.com/SolarifyDev/Squid/releases/download/2.0.0-beta.1/squid-tentacle-2.0.0-beta.1-win-x64.zip")]
    public void BuildDownloadUrl_DefaultsToGitHubReleasesZipPath(string version, string rid, string expected)
    {
        WindowsTentacleUpgradeStrategy.BuildDownloadUrl(version, rid).ShouldBe(expected);
    }

    [Theory]
    [InlineData("https://mirror.acme.internal/squid", "1.6.0", "win-x64", "https://mirror.acme.internal/squid/1.6.0/squid-tentacle-1.6.0-win-x64.zip")]
    [InlineData("https://s3.example.com/squid-mirror/", "1.6.0", "win-arm64", "https://s3.example.com/squid-mirror/1.6.0/squid-tentacle-1.6.0-win-arm64.zip")]
    public void BuildDownloadUrl_EnvOverride_RetargetsToOperatorMirror(string baseUrl, string version, string rid, string expected)
    {
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.DownloadBaseUrlEnvVar, baseUrl);

        WindowsTentacleUpgradeStrategy.BuildDownloadUrl(version, rid).ShouldBe(expected);
    }

    [Fact]
    public void ResolveDownloadBaseUrl_StripsTrailingSlash()
    {
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.DownloadBaseUrlEnvVar, "https://mirror.acme.internal/path/");

        WindowsTentacleUpgradeStrategy.ResolveDownloadBaseUrl().ShouldBe("https://mirror.acme.internal/path");
    }

    [Fact]
    public void ResolveDownloadBaseUrl_NonHttpsOverride_EmitsWarning_ButHonoursOperatorChoice()
    {
        var originalLogger = Serilog.Log.Logger;
        var sink = new CapturingLogSink();
        Serilog.Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        try
        {
            SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.DownloadBaseUrlEnvVar, "http://mirror.internal/squid");

            var resolved = WindowsTentacleUpgradeStrategy.ResolveDownloadBaseUrl();

            resolved.ShouldBe("http://mirror.internal/squid");
            sink.Events.ShouldContain(
                e => e.Level == Serilog.Events.LogEventLevel.Warning
                    && e.MessageTemplate.Text.Contains("non-HTTPS")
                    && e.MessageTemplate.Text.Contains("MITM"));
        }
        finally
        {
            Serilog.Log.Logger = originalLogger;
        }
    }

    [Fact]
    public void ResolveHealthcheckUrl_NoOverride_ReturnsDefault()
    {
        WindowsTentacleUpgradeStrategy.ResolveHealthcheckUrl().ShouldBe("http://127.0.0.1:8080/healthz");
    }

    [Theory]
    [InlineData("http://127.0.0.1:9090/api/health", "http://127.0.0.1:9090/api/health")]
    [InlineData("http://127.0.0.1:8080/custom/", "http://127.0.0.1:8080/custom")]
    [InlineData("  http://127.0.0.1:8080/healthz  ", "http://127.0.0.1:8080/healthz")]
    public void ResolveHealthcheckUrl_WithOverride_NormalisesAndReturns(string overrideValue, string expected)
    {
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.HealthcheckUrlEnvVar, overrideValue);

        WindowsTentacleUpgradeStrategy.ResolveHealthcheckUrl().ShouldBe(expected);
    }

    /// <summary>Minimal in-memory Serilog sink mirroring the helper in LinuxTentacleUpgradeStrategyTests.</summary>
    private sealed class CapturingLogSink : Serilog.Core.ILogEventSink
    {
        public List<Serilog.Events.LogEvent> Events { get; } = new();

        public void Emit(Serilog.Events.LogEvent logEvent) => Events.Add(logEvent);
    }

    // ========================================================================
    // Inner script template safety — every {{Placeholder}} substituted, every
    // method snippet injected, $RID stays as PowerShell variable.
    // ========================================================================

    [Fact]
    public void RenderInnerScript_AllPlaceholdersSubstituted_NoStaleTokensLeft()
    {
        var inner = WindowsTentacleUpgradeStrategy.RenderInnerScript("1.6.0", WindowsTentacleUpgradeStrategy.DefaultMethodOrder);

        inner.ShouldNotContain("{{TARGET_VERSION}}");
        inner.ShouldNotContain("{{DOWNLOAD_URL}}");
        inner.ShouldNotContain("{{EXPECTED_SHA256}}");
        inner.ShouldNotContain("{{INSTALL_DIR}}");
        inner.ShouldNotContain("{{SERVICE_NAME}}");
        inner.ShouldNotContain("{{HEALTHCHECK_URL}}");
        inner.ShouldNotContain("{{INSTALL_METHODS}}");

        // Catch-all: any `{{ALL_CAPS_OR_DIGIT_TOKEN}}` left is a stale placeholder.
        // Charset MUST include digits — pre-J.E.3.1 used [A-Z_]+ which silently
        // missed EXPECTED_SHA256 (digits in the placeholder name). The narrow
        // regex meant a future polish-introduced numeric placeholder could
        // ship un-substituted without detection.
        var stalePlaceholderPattern = new System.Text.RegularExpressions.Regex(@"\{\{[A-Z0-9_]+\}\}");
        stalePlaceholderPattern.IsMatch(inner).ShouldBeFalse(
            customMessage: "an unsubstituted '{{ALL_CAPS}}' placeholder ships a broken inner script — add a per-placeholder check above when introducing new placeholders");
    }

    // ========================================================================
    // J.E.3.1 — production-bug pin
    //
    // Each `{{PLACEHOLDER}}` token MUST appear EXACTLY ONCE in the template.
    // A duplicate occurrence (e.g. inside a `#`-prefixed comment that mentions
    // the placeholder name) gets `String.Replace`'d alongside the real one,
    // which splices multi-line PowerShell into a single-line comment and
    // produces parse errors that ONLY surface at agent-side Task Scheduler
    // invocation. The wrapper exits 0 (its work was done — schtasks
    // registered the task), the strategy maps to MachineUpgradeStatus.Initiated,
    // and the operator sees "Initiated" in the UI — but no actual upgrade
    // happens and last-upgrade.json is never written.
    //
    // Caught J.E.3 first attempt by the high-fidelity E2E tests in
    // TentacleUpgradeLifecycleE2ETests on the Windows runner. PR #197 fixed
    // the `{{INSTALL_METHODS}}` comment occurrence; this test pins so a future
    // polish doesn't reintroduce the same class of bug for a different
    // placeholder.
    // ========================================================================

    [Fact]
    public void RenderInnerScript_PlaceholderTokens_AppearExactlyOnceInTemplate()
    {
        // Read the embedded template directly via the same resource manifest
        // path the strategy uses. We assert against the *raw template* (pre-
        // substitution) because once Replace runs, ALL occurrences are gone
        // — the bug is invisible after substitution.
        var asm = typeof(WindowsTentacleUpgradeStrategy).Assembly;
        using var stream = asm.GetManifestResourceStream("Squid.Core.Resources.Upgrade.upgrade-windows-tentacle.ps1")
            ?? throw new System.IO.InvalidDataException("embedded template not found");
        using var reader = new System.IO.StreamReader(stream);
        var template = reader.ReadToEnd();

        // Pull every {{TOKEN}} occurrence — charset includes digits to cover
        // EXPECTED_SHA256 + future numeric-suffixed placeholders.
        var matches = System.Text.RegularExpressions.Regex.Matches(template, @"\{\{([A-Z0-9_]+)\}\}");
        var occurrencesByName = matches
            .Cast<System.Text.RegularExpressions.Match>()
            .GroupBy(m => m.Groups[1].Value)
            .ToDictionary(g => g.Key, g => g.Count());

        // Sanity: every placeholder the strategy substitutes is also present
        // in the template (drift detector — strategy can't substitute a
        // placeholder that doesn't exist).
        var strategySubstituted = new[]
        {
            "TARGET_VERSION", "DOWNLOAD_URL", "EXPECTED_SHA256",
            "INSTALL_DIR", "SERVICE_NAME", "HEALTHCHECK_URL", "HEALTHCHECK_RETRIES",
            "HEALTHCHECK_FATAL", "SERVICE_TIMEOUT_SECONDS", "INSTALL_METHODS"
        };

        foreach (var name in strategySubstituted)
        {
            occurrencesByName.ContainsKey(name).ShouldBeTrue(
                customMessage: $"strategy substitutes '{{{{{name}}}}}' but the template doesn't contain it. " +
                              $"Either the strategy renamed without updating the .ps1 OR the .ps1 dropped the placeholder. Either way the rendered inner has a hole.");

            occurrencesByName[name].ShouldBe(1,
                customMessage: $"placeholder '{{{{{name}}}}}' appears {occurrencesByName[name]} times in upgrade-windows-tentacle.ps1 — MUST be exactly once. " +
                              $"String.Replace substitutes EVERY occurrence, so a comment-line mention of the placeholder name gets rewritten too. " +
                              $"For multi-line substitutions (e.g. INSTALL_METHODS → if-block), this splices PowerShell code into a `#`-prefixed comment line — " +
                              $"PowerShell parses the FIRST line as comment but treats subsequent lines as orphan code, producing 'Try statement is missing its Catch or Finally' / 'Unexpected token' errors. " +
                              $"The wrapper still exits 0 (schtasks registered the task), strategy maps to Initiated, but the agent's inner script parse-fails and last-upgrade.json is never written. " +
                              $"FIX: edit upgrade-windows-tentacle.ps1 to remove the duplicate occurrence (typically a comment-line mention of the placeholder by name).");
        }

        // Catch-all: ANY placeholder appearing more than once is the same bug.
        // Includes future placeholders the strategy doesn't yet substitute.
        foreach (var (name, count) in occurrencesByName)
        {
            count.ShouldBe(1,
                customMessage: $"placeholder '{{{{{name}}}}}' appears {count} times in template. See above customMessage for full diagnosis — same root cause whether or not the strategy currently substitutes it.");
        }
    }

    // ========================================================================
    // J.E.3.1 — production-bug pin #2
    //
    // The .ps1's SHA256 verification MUST use direct .NET API, NOT the
    // `Get-FileHash` cmdlet. The cmdlet lives in `Microsoft.PowerShell.Utility`
    // and is loaded via the auto-loader; on some Windows runner images +
    // stripped-down PowerShell installations the auto-loader fails to find
    // `Get-FileHash` even when `Invoke-WebRequest` (same module) loads
    // fine — root cause likely a partial module-cache state under
    // `$ErrorActionPreference = 'Stop'` + `Set-StrictMode -Version Latest`.
    // Direct .NET (`[System.Security.Cryptography.SHA256]::Create()`) avoids
    // the auto-loader entirely AND is faster on large archives.
    //
    // Caught J.E.3.1 first attempt by the high-fidelity E2E tests; pre-J.E.3
    // no test actually ran the rendered .ps1 through to the verify step.
    // ========================================================================

    // ========================================================================
    // J.E.6 — auto-rollback contract
    //
    // The .ps1 MUST roll back to the previous binary if Phase B's
    // post-swap Start-Service throws (new binary's OnStart raised → SCM
    // marks service as failed). Without rollback, a failed upgrade leaves
    // the operator with INSTALL_DIR holding a broken binary + service in
    // Stopped state. With rollback, the agent auto-recovers to the
    // previous version and the operator sees ROLLED_BACK status in the UI.
    //
    // E2E coverage: TentacleUpgradeLifecycleE2ETests.E7u1_*.
    // This unit test pins the structural shape of the rollback contract
    // (function exists in template, key operations present, status enum
    // used) so a future polish that drops the rollback path is caught at
    // build time, not at the next operator-broken-upgrade incident.
    // ========================================================================

    [Fact]
    public void RenderInnerScript_RollbackContract_PinnedStructurally()
    {
        var asm = typeof(WindowsTentacleUpgradeStrategy).Assembly;
        using var stream = asm.GetManifestResourceStream("Squid.Core.Resources.Upgrade.upgrade-windows-tentacle.ps1")
            ?? throw new System.IO.InvalidDataException("embedded template not found");
        using var reader = new System.IO.StreamReader(stream);
        var template = reader.ReadToEnd();

        // 1. The Invoke-Rollback function MUST exist.
        template.ShouldContain("function Invoke-Rollback",
            customMessage: "Invoke-Rollback function REGRESSED. " +
                          "Without this, a failed upgrade leaves the operator's INSTALL_DIR holding a broken binary + service Stopped → manual SSH-and-restore required across every machine. " +
                          "Restore via the J.E.6 commit's pattern: function Invoke-Rollback { Stop-Service → archive broken to .failed → Move .bak → INSTALL_DIR → Start-Service old → write ROLLED_BACK }.");

        // 2. Start-Service post-swap MUST be wrapped in try/catch calling Invoke-Rollback.
        // String search for the exact pattern is brittle to whitespace; pin
        // the key tokens that MUST coexist in the start-service block.
        template.ShouldContain("Start-Service -Name $SERVICE_NAME",
            customMessage: "Start-Service call REGRESSED — Phase B no longer attempts to start the new binary");
        template.ShouldContain("Invoke-Rollback -Reason \"Start-Service post-swap failed",
            customMessage: "Start-Service catch path MUST call Invoke-Rollback. " +
                          "If absent: a Start-Service exception bubbles to the outer catch which writes 'Unexpected failure' status with exit 14 — broken binary stays in INSTALL_DIR.");

        // 3. ROLLED_BACK status enum is the documented happy-path-of-rollback outcome.
        template.ShouldContain("Write-UpgradeStatus -Status 'ROLLED_BACK'",
            customMessage: "ROLLED_BACK status emit REGRESSED — operator UI would show generic FAILED instead of the actionable rollback marker");

        // 4. ROLLBACK_CRITICAL_FAILED is the worst-case status (rollback restored .bak but old service won't start).
        template.ShouldContain("'ROLLBACK_CRITICAL_FAILED'",
            customMessage: "ROLLBACK_CRITICAL_FAILED status enum REGRESSED — without it, a fully-broken host (new AND old binary won't start) produces no actionable status");

        // 5. Exit code 8 is the documented "Start-Service post-swap failed → rollback fired" code.
        template.ShouldContain("-ExitCode 8",
            customMessage: "exit code 8 REGRESSED — operator parsing the .ps1's exit doesn't see the rollback-fired signal");

        // 6. The .failed archive path preserves the broken binary for post-mortem.
        template.ShouldContain("\"$installLeaf.failed\"",
            customMessage: ".failed archive path REGRESSED — operators need access to the broken binary to diagnose the OnStart failure. " +
                          "If absent: rollback deletes the broken binary → operator must reproduce the failure to debug it.");

        // 7. WaitForStatus('Running', ...) is what surfaces the OnStart exception synchronously.
        // Start-Service returns when SCM accepts the start; the OnStart
        // exception arrives asynchronously a few seconds later. Without
        // WaitForStatus, the catch wouldn't fire — the .ps1 would proceed
        // to the healthcheck loop with a service that's actually crashed.
        template.ShouldContain("WaitForStatus('Running'",
            customMessage: "WaitForStatus('Running', ...) REGRESSED. " +
                          "Without this synchronous wait after Start-Service, an OnStart exception arrives asynchronously and the .ps1's catch can't see it. " +
                          "Result: rollback never fires for the most common 'new binary is broken' failure mode.");
    }

    [Fact]
    public void RenderInnerScript_ShaVerifyUsesDirectDotNetApi_NotGetFileHashCmdlet()
    {
        var asm = typeof(WindowsTentacleUpgradeStrategy).Assembly;
        using var stream = asm.GetManifestResourceStream("Squid.Core.Resources.Upgrade.upgrade-windows-tentacle.ps1")
            ?? throw new System.IO.InvalidDataException("embedded template not found");
        using var reader = new System.IO.StreamReader(stream);
        var template = reader.ReadToEnd();

        // Positive pins: the direct-.NET API markers MUST be present.
        template.ShouldContain("[System.Security.Cryptography.SHA256]",
            customMessage: "SHA256 verification MUST use direct .NET API. If this assertion fails, the .ps1 reverted to the cmdlet — re-introducing the auto-loader brittleness that bit the windows-latest runner in J.E.3.1.");
        template.ShouldContain("ComputeHash",
            customMessage: "MUST invoke ComputeHash on the SHA256 instance — produces the actual digest");
        template.ShouldContain("[System.IO.File]::ReadAllBytes",
            customMessage: "MUST read the archive bytes directly via System.IO.File — no PowerShell file cmdlet abstraction layer");

        // Negative pin: the cmdlet form MUST NOT come back. A future polish
        // that "simplifies" the SHA block by reverting to Get-FileHash would
        // re-introduce the J.E.3.1 production bug.
        template.ShouldNotContain("Get-FileHash",
            customMessage: "Get-FileHash cmdlet usage REGRESSED. " +
                          "Root cause was: on some Windows runner images, `Get-FileHash` auto-loading from " +
                          "`Microsoft.PowerShell.Utility` fails with `CommandNotFoundException` even when " +
                          "`Invoke-WebRequest` (same module) loads fine — likely a partial module-cache state " +
                          "interaction with `$ErrorActionPreference = 'Stop'` + `Set-StrictMode -Version Latest`. " +
                          "FIX: revert to the direct-.NET form: " +
                          "`$sha256 = [System.Security.Cryptography.SHA256]::Create(); $bytes = [System.IO.File]::ReadAllBytes($archivePath); $hash = $sha256.ComputeHash($bytes); $sha256.Dispose(); $actualSha = ([System.BitConverter]::ToString($hash) -replace '-','').ToLower()`. " +
                          "Detected by J.E.3 high-fidelity E2E.");

        // Bonus pin: the renderer MUST inject this code into the inner so
        // the agent runs the bug-free version.
        var inner = WindowsTentacleUpgradeStrategy.RenderInnerScript("1.6.0", WindowsTentacleUpgradeStrategy.DefaultMethodOrder);
        inner.ShouldContain("[System.Security.Cryptography.SHA256]");
        inner.ShouldNotContain("Get-FileHash");
    }

    [Fact]
    public void RenderInnerScript_TargetVersionInjected_AppearsExactly()
    {
        var inner = WindowsTentacleUpgradeStrategy.RenderInnerScript("1.6.2", WindowsTentacleUpgradeStrategy.DefaultMethodOrder);

        inner.ShouldContain("$TARGET_VERSION   = '1.6.2'");
    }

    [Fact]
    public void RenderInnerScript_DownloadUrlContainsPowerShellRidVariable_NotServerSidePlaceholder()
    {
        // Server emits `$RID` (PowerShell variable) inside the URL because
        // the architecture is detected on the agent side. If literal `{RID}`
        // leaks through, the agent would try to download
        // squid-tentacle-1.6.0-{RID}.zip — guaranteed 404.
        var inner = WindowsTentacleUpgradeStrategy.RenderInnerScript("1.6.0", WindowsTentacleUpgradeStrategy.DefaultMethodOrder);

        inner.ShouldContain("$RID");
        inner.ShouldNotContain("{RID}");
    }

    [Fact]
    public void RenderInnerScript_InstallMethodsBlockInjected_ZipMarkerPresent()
    {
        var inner = WindowsTentacleUpgradeStrategy.RenderInnerScript("1.6.0", WindowsTentacleUpgradeStrategy.DefaultMethodOrder);

        // ZipUpgradeMethod.RenderDetectAndInstall emits an `[upgrade-method:zip]`
        // log tag + sets `$INSTALL_METHOD = 'zip'` — ensure both made it
        // through the {{INSTALL_METHODS}} substitution.
        inner.ShouldContain("[upgrade-method:zip]");
        inner.ShouldContain("$INSTALL_METHOD = 'zip'");
    }

    [Fact]
    public void RenderInnerScript_DefaultMethodOrder_IsZipOnlyForPhase12E4()
    {
        //  ships zip-only. Future phases prepend chocolatey/MSI.
        // Pin the count + the only-impl shape so a premature add gets caught.
        WindowsTentacleUpgradeStrategy.DefaultMethodOrder.Count.ShouldBe(1);
        WindowsTentacleUpgradeStrategy.DefaultMethodOrder[0].ShouldBeOfType<ZipUpgradeMethod>();
    }

    [Fact]
    public void RenderInnerScript_ExpectedSha256_DefaultsToEmpty_PairedWithPs1EmptySkipPolish()
    {
        // The strategy substitutes empty string for {{EXPECTED_SHA256}}; the
        // .ps1 polish at WindowsUpgradeScriptResourceTests pins that the
        // template skips verification when the value is empty. Together,
        // these mean upgrade dispatches succeed with no false-positive
        // SHA256 mismatch failures while the build pipeline doesn't yet
        // publish per-archive hashes. This test pins the strategy half;
        // WindowsUpgradeScriptResourceTests pins the template half.
        var inner = WindowsTentacleUpgradeStrategy.RenderInnerScript("1.6.0", WindowsTentacleUpgradeStrategy.DefaultMethodOrder);

        inner.ShouldContain("$EXPECTED_SHA256  = ''",
            customMessage: "strategy must substitute empty string into the SHA256 placeholder; the template's empty-skip path activates");
    }

    // ========================================================================
    // Outer wrapper shape — Halibut-connected schtasks dispatcher.
    // ========================================================================

    [Fact]
    public void BuildScript_Returns_OuterWrapper_NotInnerDirectly()
    {
        // The script body sent over Halibut MUST be the outer wrapper (which
        // schedules a detached Task Scheduler task). If the strategy ever
        // accidentally returns the inner directly, Phase B's Stop-Service
        // would kill the very process executing the script before Move-Item
        // finishes — every upgrade would corrupt the install dir.
        var script = WindowsTentacleUpgradeStrategy.BuildScript("1.6.0");

        script.ShouldContain("Register-ScheduledTask", customMessage:
            "outer wrapper must register a detached Task Scheduler task to isolate Phase B from the squid-tentacle service tree");
        script.ShouldContain("$InnerBase64", customMessage:
            "outer wrapper must embed the inner via base64 to dodge here-string lexer corner cases");
        script.ShouldContain("FromBase64String", customMessage:
            "outer wrapper must decode the embedded inner before writing it to disk");
    }

    [Fact]
    public void BuildScript_OuterWrapper_RegistersTaskWithSystemIdentityAndAutoDelete()
    {
        // SYSTEM identity (squid-tentacle service runs as LocalSystem so it
        // has the rights to schedule this) + auto-cleanup via DeleteExpired-
        // TaskAfter (replaces the legacy `/Z` flag which generates malformed
        // V2 task XML on Windows Server 2022 — see comment block in
        // BuildOuterWrapper).
        var script = WindowsTentacleUpgradeStrategy.BuildScript("1.6.0");

        script.ShouldContain("-UserId 'SYSTEM'", customMessage:
            "outer wrapper must register the task with SYSTEM identity so Phase B has Stop-Service / Move-Item rights");
        script.ShouldContain("-LogonType ServiceAccount", customMessage:
            "SYSTEM identity requires ServiceAccount logon type; without it Register-ScheduledTask refuses the principal");
        script.ShouldContain("-RunLevel Highest", customMessage:
            "task must run elevated so Phase B's sc.exe operations succeed under UAC");
        script.ShouldContain("DeleteExpiredTaskAfter", customMessage:
            "outer wrapper must auto-cleanup expired tasks so Task Scheduler library doesn't accumulate stale upgrade entries (replaces legacy /Z flag)");
        script.ShouldContain("-Force", customMessage:
            "Register-ScheduledTask must use -Force so a partially-deleted previous task with the same name can't block re-registration");
    }

    [Fact]
    public void BuildScript_OuterWrapper_DoesNotUseLegacySchtasksZFlag()
    {
        // Reverse-verify guard: a future refactor that goes back to schtasks.exe
        // /Z would re-introduce the (41,4):EndBoundary: malformed-XML error on
        // Server 2022. Pin the absence of /Z + schtasks /Create here so the
        // regression is compile/test-time visible.
        var script = WindowsTentacleUpgradeStrategy.BuildScript("1.6.0");

        script.ShouldNotContain("'/Z'", customMessage:
            "/Z + /SC ONCE generates malformed task XML on Server 2022; we use Register-ScheduledTask + DeleteExpiredTaskAfter instead");
        script.ShouldNotContain("schtasks.exe /Create", customMessage:
            "switched away from schtasks.exe direct invocation — Register-ScheduledTask cmdlet generates valid V2 task XML");
    }

    [Fact]
    public void BuildScript_OuterWrapper_TaskNameSuffixedWithGuid_CrossDispatchUniqueness()
    {
        // Two operators dispatching concurrent upgrades MUST get unique task
        // names (otherwise the second's /Create with /F overwrites the first
        // before the first runs). Mirrors the systemd-run --unit naming
        // pattern Linux uses — version + PID for uniqueness.
        var script = WindowsTentacleUpgradeStrategy.BuildScript("1.6.0");

        script.ShouldContain("[guid]::NewGuid().ToString('N')",
            customMessage: "outer wrapper must include a fresh GUID in the task name so concurrent dispatches don't collide");
    }

    [Fact]
    public void BuildScript_OuterWrapper_PowerShellExecutionPolicyBypassed()
    {
        // The detached schtasks invocation runs powershell.exe -File, but a
        // non-default ExecutionPolicy on the agent would refuse to load the
        // .ps1. -ExecutionPolicy Bypass is the standard sysadmin pattern for
        // scripts shipped over the wire (Octopus's own install scripts do
        // the same).
        var script = WindowsTentacleUpgradeStrategy.BuildScript("1.6.0");

        script.ShouldContain("-ExecutionPolicy Bypass",
            customMessage: "schtasks /TR must invoke PowerShell with -ExecutionPolicy Bypass so a strict default policy doesn't refuse the dispatch.ps1");
    }

    [Fact]
    public void BuildScript_OuterWrapper_WritesInnerToProgramDataDispatch()
    {
        // %ProgramData%\Squid\Tentacle\upgrade\dispatch-<TaskName>.ps1 —
        // contract directory + per-task inner script path. The dispatch file
        // lands as a sibling of last-upgrade.json + upgrade.log + upgrade.lock.
        // It MUST be per-task (not a shared dispatch.ps1) because two
        // concurrent dispatches would otherwise overwrite each other before
        // their tasks fire — caught by Wrapper_ConcurrentDispatches E2E.
        var script = WindowsTentacleUpgradeStrategy.BuildScript("1.6.0");

        script.ShouldContain("Squid\\Tentacle\\upgrade",
            customMessage: "outer wrapper must write to the contract dir under %ProgramData%");
        script.ShouldContain("dispatch-$TaskName.ps1",
            customMessage: "dispatch file must be per-task (interpolated $TaskName) so concurrent dispatches don't race on a single shared file");
    }

    [Fact]
    public void BuildScript_OuterWrapper_DispatchPath_IsNotASharedConstant()
    {
        // Reverse-verify guard: a future refactor that goes back to a constant
        // 'dispatch.ps1' would re-introduce the concurrent-dispatch race
        // (caught by Wrapper_ConcurrentDispatches E2E). Pin the absence of the
        // legacy literal here so the regression is compile/test-time visible.
        var script = WindowsTentacleUpgradeStrategy.BuildScript("1.6.0");

        script.ShouldNotContain("'dispatch.ps1'",
            customMessage: "constant 'dispatch.ps1' would race under concurrent wrappers; dispatch file MUST be per-task");
        script.ShouldNotContain("\"dispatch.ps1\"",
            customMessage: "constant \"dispatch.ps1\" would race under concurrent wrappers; dispatch file MUST be per-task");
    }

    [Fact]
    public void BuildScript_InnerEncodedAsBase64_RoundTripsThroughOuter()
    {
        // The inner script must be reconstructible from the outer's base64
        // payload. Without this round-trip pin, a base64-encoding bug
        // (truncation, wrong UTF8 codepage) would produce gibberish at
        // FromBase64String time and the agent would fail to parse the .ps1.
        var script = WindowsTentacleUpgradeStrategy.BuildScript("1.6.0");

        // Extract the base64 payload from the outer wrapper. The wrapper
        // assigns it to $InnerBase64 = '...';
        var match = System.Text.RegularExpressions.Regex.Match(
            script, @"\$InnerBase64 = '([^']+)'");
        match.Success.ShouldBeTrue("outer wrapper must contain $InnerBase64 = '<payload>'");

        var b64 = match.Groups[1].Value;
        var roundTripped = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));

        roundTripped.ShouldContain("$TARGET_VERSION   = '1.6.0'",
            customMessage: "decoded inner must contain the substituted target version");
        roundTripped.ShouldContain("PROCESSOR_ARCHITECTURE",
            customMessage: "decoded inner must be the upgrade-windows-tentacle.ps1 template");
        roundTripped.ShouldContain("[upgrade-method:zip]",
            customMessage: "decoded inner must include the zip-method snippet from {{INSTALL_METHODS}}");
    }

    // ========================================================================
    // StartScriptCommand shape — same discipline as Linux but PowerShell-typed.
    // ========================================================================

    [Fact]
    public void StartScriptCommand_TicketIdIncludesFullGuid()
    {
        var cmd = WindowsTentacleUpgradeStrategy.PreviewStartScriptCommand(
            new Machine { Id = 1000000000, Name = "big-id", Endpoint = "{}" },
            "1.6.0");

        cmd.ScriptTicket.TaskId.Length.ShouldBeGreaterThan(40);
        cmd.ScriptTicket.TaskId.ShouldStartWith("upgrade-1000000000-");

        var hexPart = cmd.ScriptTicket.TaskId["upgrade-1000000000-".Length..];
        hexPart.Length.ShouldBe(32);
        hexPart.ShouldMatch("^[0-9a-f]{32}$");
    }

    [Fact]
    public void StartScriptCommand_IsolationAndSyntaxPinned_FullIsolationPowerShell()
    {
        var cmd = WindowsTentacleUpgradeStrategy.PreviewStartScriptCommand(
            new Machine { Id = 7, Name = "m", Endpoint = "{}" },
            "1.6.0");

        cmd.Isolation.ShouldBe(ScriptIsolationLevel.FullIsolation,
            "FullIsolation lets the agent's ScriptIsolationMutex serialize upgrades behind active deployments");
        cmd.ScriptSyntax.ShouldBe(ScriptType.PowerShell,
            "Windows must dispatch as PowerShell — Bash would be silently rejected by the agent's executor");
        cmd.IsolationMutexName.ShouldBe(ScriptIsolationMutexNames.ForMachine(7),
            customMessage: "upgrade must share the machine-scoped mutex name deployments use, so the agent " +
                           "serializes the (service-restarting) upgrade behind any in-flight deployment script");
    }

    [Fact]
    public void StartScriptCommand_TwoInvocationsForSameMachine_ProduceDifferentTickets()
    {
        var m = new Machine { Id = 7, Name = "m", Endpoint = "{}" };

        var a = WindowsTentacleUpgradeStrategy.PreviewStartScriptCommand(m, "1.6.0");
        var b = WindowsTentacleUpgradeStrategy.PreviewStartScriptCommand(m, "1.6.0");

        a.ScriptTicket.TaskId.ShouldNotBe(b.ScriptTicket.TaskId);
    }

    // ========================================================================
    // Halibut dispatch branches — mirror Linux's dispatchAcked discipline +
    // the Windows-specific exit-0-means-Initiated divergence.
    // ========================================================================

    private static Machine MakeMachine(int id, string style, string subscriptionId, string thumbprint)
    {
        var endpoint = $"{{\"CommunicationStyle\":\"{style}\",\"SubscriptionId\":\"{subscriptionId}\",\"Thumbprint\":\"{thumbprint}\"}}";
        return new Machine { Id = id, Name = $"machine-{id}", Endpoint = endpoint, SpaceId = 1 };
    }

    private static ScriptStatusResponse FakeStartResponse()
        => new(new ScriptTicket("t"), ProcessState.Pending, exitCode: 0, logs: new List<ProcessOutput>(), nextLogSequence: 0);

    [Fact]
    public async Task UpgradeAsync_NullMachine_ReturnsFailedWithoutDispatch()
    {
        var strategy = new WindowsTentacleUpgradeStrategy(halibutClientFactory: null, observer: null);

        var outcome = await strategy.UpgradeAsync(machine: null, targetVersion: "1.6.0", CancellationToken.None);

        outcome.Status.ShouldBe(MachineUpgradeStatus.Failed);
        outcome.AgentVersionMayHaveChanged.ShouldBeFalse();
    }

    [Fact]
    public async Task UpgradeAsync_BlankTargetVersion_ReturnsFailed()
    {
        var strategy = new WindowsTentacleUpgradeStrategy(halibutClientFactory: null, observer: null);
        var machine = MakeMachine(1, "TentaclePolling", "sub-1", "AABB");

        var outcome = await strategy.UpgradeAsync(machine, targetVersion: "  ", CancellationToken.None);

        outcome.Status.ShouldBe(MachineUpgradeStatus.Failed);
        outcome.Detail.ShouldContain("targetVersion");
    }

    [Fact]
    public async Task UpgradeAsync_UnparseableEndpoint_ReturnsFailedWithoutDispatch()
    {
        var halibut = new Mock<IHalibutClientFactory>(MockBehavior.Strict);
        var observer = new Mock<IHalibutScriptObserver>(MockBehavior.Strict);
        var strategy = new WindowsTentacleUpgradeStrategy(halibut.Object, observer.Object);
        var brokenMachine = new Machine { Id = 1, Name = "broken", Endpoint = "{}", SpaceId = 1 };

        var outcome = await strategy.UpgradeAsync(brokenMachine, "1.6.0", CancellationToken.None);

        outcome.Status.ShouldBe(MachineUpgradeStatus.Failed);
        outcome.Detail.ShouldContain("Halibut endpoint");
        halibut.VerifyNoOtherCalls();
        observer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpgradeAsync_ScriptSuccess_ReturnsInitiatedNotUpgraded()
    {
        // CRITICAL Windows divergence from Linux: outer wrapper exits 0
        // BEFORE the detached upgrade runs. result.Success=true means the
        // wrapper successfully scheduled the Task Scheduler task — NOT that
        // the upgrade is complete. Map to Initiated (not Upgraded). The
        // actual outcome arrives via last-upgrade.json on the next health
        // check; rapid-polling burst (already wired) catches it.
        var (strategy, _, observer) = BuildMockedStrategy();
        var machine = MakeMachine(2, "TentaclePolling", "sub-2", "AABB");

        observer
            .Setup(o => o.ObserveAndCompleteAsync(It.IsAny<Machine>(), It.IsAny<IAsyncScriptService>(), It.IsAny<ScriptTicket>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<ScriptStatusResponse>(), It.IsAny<ServiceEndPoint>(), It.IsAny<ScriptOutputSink>()))
            .ReturnsAsync(new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = new List<string> { "[upgrade-wrapper] Task triggered" } });

        var outcome = await strategy.UpgradeAsync(machine, "1.6.0", CancellationToken.None);

        outcome.Status.ShouldBe(MachineUpgradeStatus.Initiated,
            "Windows ExitCode=0 means task scheduled, not upgrade complete — must NOT map to Upgraded");
        outcome.Detail.ShouldContain("1.6.0");
        outcome.Detail.ShouldContain("Task Scheduler",
            customMessage: "operator must understand the upgrade is in a detached scheduler task and outcome arrives later");
        outcome.AgentVersionMayHaveChanged.ShouldBeTrue(
            "wrapper success → detached task likely runs to completion → cache must refresh once the new version reports back");
    }

    [Fact]
    public async Task UpgradeAsync_ScriptNonZeroExit_ReturnsFailedWithLastLog()
    {
        var (strategy, _, observer) = BuildMockedStrategy();
        var machine = MakeMachine(3, "TentaclePolling", "sub-3", "AABB");

        observer
            .Setup(o => o.ObserveAndCompleteAsync(It.IsAny<Machine>(), It.IsAny<IAsyncScriptService>(), It.IsAny<ScriptTicket>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<ScriptStatusResponse>(), It.IsAny<ServiceEndPoint>(), It.IsAny<ScriptOutputSink>()))
            .ReturnsAsync(new ScriptExecutionResult { Success = false, ExitCode = 1, LogLines = new List<string> { "Connecting...", "::error:: schtasks /Create failed" } });

        var outcome = await strategy.UpgradeAsync(machine, "1.6.0", CancellationToken.None);

        outcome.Status.ShouldBe(MachineUpgradeStatus.Failed);
        outcome.Detail.ShouldContain("exit 1");
        outcome.Detail.ShouldContain("schtasks /Create failed");
        outcome.AgentVersionMayHaveChanged.ShouldBeFalse(
            "wrapper failure means schtasks didn't register the detached task → no binary swap occurred → cache stays valid");
    }

    [Fact]
    public async Task UpgradeAsync_ScriptFailedWithEmptyLogs_DoesNotIndexExceptionOnLastLog()
    {
        var (strategy, _, observer) = BuildMockedStrategy();
        var machine = MakeMachine(4, "TentaclePolling", "sub-4", "AABB");

        observer
            .Setup(o => o.ObserveAndCompleteAsync(It.IsAny<Machine>(), It.IsAny<IAsyncScriptService>(), It.IsAny<ScriptTicket>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<ScriptStatusResponse>(), It.IsAny<ServiceEndPoint>(), It.IsAny<ScriptOutputSink>()))
            .ReturnsAsync(new ScriptExecutionResult { Success = false, ExitCode = 1, LogLines = new List<string>() });

        var outcome = await strategy.UpgradeAsync(machine, "1.6.0", CancellationToken.None);

        outcome.Status.ShouldBe(MachineUpgradeStatus.Failed);
        outcome.Detail.ShouldContain("(no log lines)");
    }

    [Fact]
    public async Task UpgradeAsync_HalibutDisconnectAfterDispatch_TreatedAsInitiated()
    {
        var (strategy, _, observer) = BuildMockedStrategy();
        var machine = MakeMachine(5, "TentaclePolling", "sub-5", "AABB");

        observer
            .Setup(o => o.ObserveAndCompleteAsync(It.IsAny<Machine>(), It.IsAny<IAsyncScriptService>(), It.IsAny<ScriptTicket>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<ScriptStatusResponse>(), It.IsAny<ServiceEndPoint>(), It.IsAny<ScriptOutputSink>()))
            .ThrowsAsync(new HalibutClientException("connection closed"));

        var outcome = await strategy.UpgradeAsync(machine, "1.6.0", CancellationToken.None);

        outcome.Status.ShouldBe(MachineUpgradeStatus.Initiated,
            "mid-wrapper Halibut disconnect = task may have registered before agent went away → Initiated, NOT Failed");
        outcome.Detail.ShouldContain("disconnect");
        outcome.AgentVersionMayHaveChanged.ShouldBeTrue();
    }

    [Fact]
    public async Task UpgradeAsync_LivenessProbeAbortAfterDispatch_TreatedAsInitiated()
    {
        var (strategy, _, observer) = BuildMockedStrategy();
        var machine = MakeMachine(25, "TentaclePolling", "sub-25", "AABB");

        observer
            .Setup(o => o.ObserveAndCompleteAsync(It.IsAny<Machine>(), It.IsAny<IAsyncScriptService>(), It.IsAny<ScriptTicket>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<ScriptStatusResponse>(), It.IsAny<ServiceEndPoint>(), It.IsAny<ScriptOutputSink>()))
            .ThrowsAsync(new AgentUnreachableException("win-agent", consecutiveFailures: 2));

        var outcome = await strategy.UpgradeAsync(machine, "1.6.0", CancellationToken.None);

        outcome.Status.ShouldBe(MachineUpgradeStatus.Initiated);
        outcome.Detail.ShouldContain("liveness");
        outcome.Detail.ShouldContain("2");
        outcome.AgentVersionMayHaveChanged.ShouldBeTrue();
    }

    [Fact]
    public async Task UpgradeAsync_HalibutDisconnectBeforeDispatchAcked_ReturnsFailedNotInitiated()
    {
        var halibut = new Mock<IHalibutClientFactory>();
        var observer = new Mock<IHalibutScriptObserver>(MockBehavior.Strict);
        var scriptClient = new Mock<IAsyncScriptService>();

        scriptClient
            .Setup(s => s.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .ThrowsAsync(new HalibutClientException("Polling endpoint not registered (TLS handshake failed)"));

        halibut
            .Setup(f => f.CreateClient(It.IsAny<ServiceEndPoint>()))
            .Returns(scriptClient.Object);

        var strategy = new WindowsTentacleUpgradeStrategy(halibut.Object, observer.Object);
        var machine = MakeMachine(15, "TentaclePolling", "sub-15", "AABB");

        var outcome = await strategy.UpgradeAsync(machine, "1.6.0", CancellationToken.None);

        outcome.Status.ShouldBe(MachineUpgradeStatus.Failed,
            "StartScriptAsync threw → wrapper was NEVER dispatched → must NOT report Initiated");
        outcome.Detail.ShouldContain("dispatch");
        outcome.AgentVersionMayHaveChanged.ShouldBeFalse();
        observer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpgradeAsync_OperationCancelled_RethrowsInsteadOfSwallowing()
    {
        var (strategy, _, observer) = BuildMockedStrategy();
        var machine = MakeMachine(6, "TentaclePolling", "sub-6", "AABB");

        observer
            .Setup(o => o.ObserveAndCompleteAsync(It.IsAny<Machine>(), It.IsAny<IAsyncScriptService>(), It.IsAny<ScriptTicket>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<ScriptStatusResponse>(), It.IsAny<ServiceEndPoint>(), It.IsAny<ScriptOutputSink>()))
            .ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(() =>
            strategy.UpgradeAsync(machine, "1.6.0", CancellationToken.None));
    }

    [Fact]
    public async Task UpgradeAsync_GenericException_ReturnsFailedWithExceptionTypeAndMessage()
    {
        var (strategy, _, observer) = BuildMockedStrategy();
        var machine = MakeMachine(7, "TentaclePolling", "sub-7", "AABB");

        observer
            .Setup(o => o.ObserveAndCompleteAsync(It.IsAny<Machine>(), It.IsAny<IAsyncScriptService>(), It.IsAny<ScriptTicket>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<ScriptStatusResponse>(), It.IsAny<ServiceEndPoint>(), It.IsAny<ScriptOutputSink>()))
            .ThrowsAsync(new InvalidOperationException("observer state corrupted"));

        var outcome = await strategy.UpgradeAsync(machine, "1.6.0", CancellationToken.None);

        outcome.Status.ShouldBe(MachineUpgradeStatus.Failed);
        outcome.Detail.ShouldContain("InvalidOperationException");
        outcome.Detail.ShouldContain("observer state corrupted");
    }

    private static (WindowsTentacleUpgradeStrategy strategy, Mock<IHalibutClientFactory> halibut, Mock<IHalibutScriptObserver> observer) BuildMockedStrategy()
    {
        var halibut = new Mock<IHalibutClientFactory>();
        var observer = new Mock<IHalibutScriptObserver>();
        var scriptClient = new Mock<IAsyncScriptService>();

        scriptClient
            .Setup(s => s.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .ReturnsAsync(FakeStartResponse());

        halibut
            .Setup(f => f.CreateClient(It.IsAny<ServiceEndPoint>()))
            .Returns(scriptClient.Object);

        return (new WindowsTentacleUpgradeStrategy(halibut.Object, observer.Object), halibut, observer);
    }

    // ── Versioned (blue-green) upgrade ──────────────────────────────────────

    [Fact]
    public void RenderInnerScript_Versioned_DetectsCurrentJunctionAndCapturesAnchor()
    {
        var inner = WindowsTentacleUpgradeStrategy.RenderInnerScript("1.6.0", WindowsTentacleUpgradeStrategy.DefaultMethodOrder);

        inner.ShouldContain("[System.IO.FileAttributes]::ReparsePoint",
            customMessage: "must detect the versioned layout by testing whether $INSTALL_DIR\\current is a reparse point (junction)");
        inner.ShouldContain("$isVersioned = $true");
        inner.ShouldContain("$oldVerTarget = ",
            customMessage: "must capture the previous version dir (the rollback anchor) from the current junction target");
    }

    [Fact]
    public void RenderInnerScript_Versioned_SwapsViaJunctionWithoutTouchingRunningVersion()
    {
        var inner = WindowsTentacleUpgradeStrategy.RenderInnerScript("1.6.0", WindowsTentacleUpgradeStrategy.DefaultMethodOrder);

        inner.ShouldContain("$newVerDir = Join-Path $versionsRoot $TARGET_VERSION",
            customMessage: "the new version must be staged into versions\\<target>, not over the running version");
        inner.ShouldContain("($newFull -eq $oldFull)",
            customMessage: "a re-upgrade to the already-active version must be a no-op — the running version directory is never deleted/overwritten (it IS the active target)");
        inner.ShouldContain("[System.IO.Directory]::Delete($currentPointer, $false)",
            customMessage: "the old junction must be removed non-recursively so the target version's files are never wiped");
        inner.ShouldContain("New-Item -ItemType Junction -Path $currentPointer -Target $newVerDir",
            customMessage: "current must be repointed at the new version via a junction");
    }

    [Fact]
    public void RenderInnerScript_Versioned_GC_PrunesOldVersionsKeepingNewest()
    {
        var inner = WindowsTentacleUpgradeStrategy.RenderInnerScript("1.6.0", WindowsTentacleUpgradeStrategy.DefaultMethodOrder);

        inner.ShouldContain("SQUID_UPGRADE_KEEP_VERSIONS",
            customMessage: "version GC retention must be tunable via the SQUID_UPGRADE_KEEP_VERSIONS env var");
        inner.ShouldContain("if ($keep -lt 2) { $keep = 2 }",
            customMessage: "GC must floor retention at 2 (active + a rollback target) so a bad value can't strand rollback");
        inner.ShouldContain("Sort-Object CreationTimeUtc -Descending",
            customMessage: "GC must enumerate version dirs newest-first (by install/creation time) to keep the newest N and prune older");
    }

    [Fact]
    public void RenderInnerScript_Versioned_SameVersionReupgrade_IsNoOp()
    {
        var inner = WindowsTentacleUpgradeStrategy.RenderInnerScript("1.6.0", WindowsTentacleUpgradeStrategy.DefaultMethodOrder);

        inner.ShouldContain("already-active",
            customMessage: "a re-upgrade to the already-active version must emit an `already-active` event and skip stage+repoint (no extract residue in the live version dir)");
    }

    [Fact]
    public void RenderInnerScript_Versioned_RollbackRepointsToPreviousVersion_AndKeepsItIntact()
    {
        var inner = WindowsTentacleUpgradeStrategy.RenderInnerScript("1.6.0", WindowsTentacleUpgradeStrategy.DefaultMethodOrder);

        inner.ShouldContain("New-Item -ItemType Junction -Path $currentPointer -Target $oldVerTarget",
            customMessage: "the versioned rollback must repoint current back to the previous version dir ($oldVerTarget)");
        inner.ShouldContain("previous version intact at $oldVerTarget",
            customMessage: "ROLLBACK_CRITICAL_FAILED status must state the previous version is still intact on disk");
    }

    [Fact]
    public void RenderInnerScript_FlatPathUnchanged_StillHasBakAndFailedSwap()
    {
        // Non-breaking guard: the flat (.bak / .failed) swap + restore must still be
        // present — the blue-green path is purely additive, gated on $isVersioned.
        var inner = WindowsTentacleUpgradeStrategy.RenderInnerScript("1.6.0", WindowsTentacleUpgradeStrategy.DefaultMethodOrder);

        inner.ShouldContain("$installLeaf.bak",
            customMessage: "the flat .bak swap must remain for non-versioned installs (non-breaking)");
        inner.ShouldContain("$installLeaf.failed",
            customMessage: "the flat rollback's .failed post-mortem archive must remain for non-versioned installs");
    }
}
