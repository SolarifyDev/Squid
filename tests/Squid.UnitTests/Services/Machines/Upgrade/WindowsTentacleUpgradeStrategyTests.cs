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

    public WindowsTentacleUpgradeStrategyTests()
    {
        _previousBaseUrlOverride = SystemEnvironment.GetEnvironmentVariable(WindowsTentacleUpgradeStrategy.DownloadBaseUrlEnvVar);
        _previousHealthcheckUrlOverride = SystemEnvironment.GetEnvironmentVariable(WindowsTentacleUpgradeStrategy.HealthcheckUrlEnvVar);

        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.DownloadBaseUrlEnvVar, null);
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.HealthcheckUrlEnvVar, null);
    }

    public void Dispose()
    {
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.DownloadBaseUrlEnvVar, _previousBaseUrlOverride);
        SystemEnvironment.SetEnvironmentVariable(WindowsTentacleUpgradeStrategy.HealthcheckUrlEnvVar, _previousHealthcheckUrlOverride);
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
            "INSTALL_DIR", "SERVICE_NAME", "HEALTHCHECK_URL", "INSTALL_METHODS"
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
            .Setup(o => o.ObserveAndCompleteAsync(It.IsAny<Machine>(), It.IsAny<IAsyncScriptService>(), It.IsAny<ScriptTicket>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<ScriptStatusResponse>(), It.IsAny<ServiceEndPoint>()))
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
            .Setup(o => o.ObserveAndCompleteAsync(It.IsAny<Machine>(), It.IsAny<IAsyncScriptService>(), It.IsAny<ScriptTicket>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<ScriptStatusResponse>(), It.IsAny<ServiceEndPoint>()))
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
            .Setup(o => o.ObserveAndCompleteAsync(It.IsAny<Machine>(), It.IsAny<IAsyncScriptService>(), It.IsAny<ScriptTicket>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<ScriptStatusResponse>(), It.IsAny<ServiceEndPoint>()))
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
            .Setup(o => o.ObserveAndCompleteAsync(It.IsAny<Machine>(), It.IsAny<IAsyncScriptService>(), It.IsAny<ScriptTicket>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<ScriptStatusResponse>(), It.IsAny<ServiceEndPoint>()))
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
            .Setup(o => o.ObserveAndCompleteAsync(It.IsAny<Machine>(), It.IsAny<IAsyncScriptService>(), It.IsAny<ScriptTicket>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<ScriptStatusResponse>(), It.IsAny<ServiceEndPoint>()))
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
            .Setup(o => o.ObserveAndCompleteAsync(It.IsAny<Machine>(), It.IsAny<IAsyncScriptService>(), It.IsAny<ScriptTicket>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<ScriptStatusResponse>(), It.IsAny<ServiceEndPoint>()))
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
            .Setup(o => o.ObserveAndCompleteAsync(It.IsAny<Machine>(), It.IsAny<IAsyncScriptService>(), It.IsAny<ScriptTicket>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<ScriptStatusResponse>(), It.IsAny<ServiceEndPoint>()))
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
}
