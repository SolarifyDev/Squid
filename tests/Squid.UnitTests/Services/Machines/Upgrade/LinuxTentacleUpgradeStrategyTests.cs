using Halibut;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Machines.Upgrade;
using Squid.Message.Commands.Machine;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Enums;
// Disambiguate: `using Squid.Core.Persistence.Entities.Deployments` brings in
// the entity `Environment` which collides with `System.Environment`. Alias
// system one for the env-var helpers used in the URL-override tests.
using SystemEnvironment = System.Environment;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// Coverage for <see cref="LinuxTentacleUpgradeStrategy"/>:
///
/// <list type="bullet">
///   <item><b>Pure functions</b> (URL pattern, env override, embedded script
///         template substitution) — no infra dependency.</item>
///   <item><b>Halibut dispatch branches</b> (audit H-10) — mocked
///         <c>IHalibutClientFactory</c> + <c>IHalibutScriptObserver</c>
///         drive every outcome path: success → Upgraded, non-zero exit →
///         Failed (incl. empty-logs guard), <c>HalibutClientException</c>
///         mid-restart → Initiated, <c>OperationCanceledException</c> →
///         rethrows untouched, generic <c>Exception</c> → Failed.</item>
///   <item><b>BuildScript template safety</b> (audit H-11) — every
///         <c>{{Placeholder}}</c> is substituted, <c>{RID}</c> rewritten to
///         the bash-side <c>$RID</c>, no stale tokens left.</item>
/// </list>
/// </summary>
[Collection(Squid.UnitTests.Support.GlobalStateSerialisedCollection.Name)]
public sealed class LinuxTentacleUpgradeStrategyTests : IDisposable
{
    private readonly string _previousBaseUrlOverride;
    private readonly string _previousHealthcheckUrlOverride;
    private readonly string _previousStateDirOverride;
    private readonly string _previousHealthcheckRetriesOverride;

    public LinuxTentacleUpgradeStrategyTests()
    {
        _previousBaseUrlOverride = SystemEnvironment.GetEnvironmentVariable(LinuxTentacleUpgradeStrategy.DownloadBaseUrlEnvVar);
        _previousHealthcheckUrlOverride = SystemEnvironment.GetEnvironmentVariable(LinuxTentacleUpgradeStrategy.HealthcheckUrlEnvVar);
        _previousStateDirOverride = SystemEnvironment.GetEnvironmentVariable(LinuxTentacleUpgradeStrategy.StateDirEnvVar);
        _previousHealthcheckRetriesOverride = SystemEnvironment.GetEnvironmentVariable(LinuxTentacleUpgradeStrategy.HealthcheckRetriesEnvVar);

        SystemEnvironment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.DownloadBaseUrlEnvVar, null);
        SystemEnvironment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.HealthcheckUrlEnvVar, null);
        SystemEnvironment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.StateDirEnvVar, null);
        SystemEnvironment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.HealthcheckRetriesEnvVar, null);
    }

    public void Dispose()
    {
        SystemEnvironment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.DownloadBaseUrlEnvVar, _previousBaseUrlOverride);
        SystemEnvironment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.HealthcheckUrlEnvVar, _previousHealthcheckUrlOverride);
        SystemEnvironment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.StateDirEnvVar, _previousStateDirOverride);
        SystemEnvironment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.HealthcheckRetriesEnvVar, _previousHealthcheckRetriesOverride);
    }

    [Fact]
    public void DownloadBaseUrlEnvVar_ConstantNamePinned()
    {
        // Renaming this constant breaks every air-gapped operator who pinned
        // a private mirror via env. Hard-pin in test.
        LinuxTentacleUpgradeStrategy.DownloadBaseUrlEnvVar.ShouldBe("SQUID_TARGET_LINUX_TENTACLE_DOWNLOAD_BASE_URL");
    }

    // ── J.L.E.2: StateDirEnvVar pins ────────────────────────────────────────

    [Fact]
    public void StateDirEnvVar_ConstantNamePinned()
    {
        // Renaming this constant breaks operators with FHS-strict
        // deployments / container volume mounts who tuned state path.
        // Hard-pin in test.
        LinuxTentacleUpgradeStrategy.StateDirEnvVar
            .ShouldBe("SQUID_TARGET_LINUX_TENTACLE_STATE_DIR");
    }

    [Fact]
    public void ResolveStateDir_NoEnvVar_ReturnsDefaultVarLibPath()
    {
        // Default `/var/lib/squid-tentacle` matches what install-tentacle.sh
        // creates + chowns. Pin the default literally — a polish that
        // changes this would mass-break every default-install deployment
        // (last-upgrade.json wouldn't be findable by the existing agent
        // CapabilitiesService that reads it).
        LinuxTentacleUpgradeStrategy.ResolveStateDir().ShouldBe("/var/lib/squid-tentacle");
    }

    [Theory]
    [InlineData("/srv/squid-tentacle", "/srv/squid-tentacle")]              // FHS-strict deployment
    [InlineData("/data/squid-tentacle", "/data/squid-tentacle")]            // container volume mount
    [InlineData("/tmp/test-isolated/squid-tentacle", "/tmp/test-isolated/squid-tentacle")]  // E2E test isolation
    [InlineData("/var/lib/squid-tentacle/", "/var/lib/squid-tentacle")]     // trailing slash trimmed
    [InlineData("  /custom/path  ", "/custom/path")]                        // whitespace trimmed
    public void ResolveStateDir_OperatorOverride_NormalisesAndReturns(string envValue, string expected)
    {
        SystemEnvironment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.StateDirEnvVar, envValue);

        LinuxTentacleUpgradeStrategy.ResolveStateDir().ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]                          // empty after trim
    [InlineData("   ")]                       // whitespace-only
    [InlineData("\t\n")]                      // other whitespace
    public void ResolveStateDir_EmptyOrWhitespace_FallsBackToDefault(string envValue)
    {
        SystemEnvironment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.StateDirEnvVar, envValue);

        // Empty/whitespace falls back to the safe default. Operator-friendly:
        // an unset-but-defined env var (e.g. `export FOO=` in a parent shell)
        // doesn't break the upgrade flow with bad-path errors.
        LinuxTentacleUpgradeStrategy.ResolveStateDir().ShouldBe("/var/lib/squid-tentacle");
    }

    [Fact]
    public void BuildScript_StateDirPlaceholder_SubstitutedFromEnv()
    {
        SystemEnvironment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.StateDirEnvVar, "/srv/test-isolated/state");

        var rendered = LinuxTentacleUpgradeStrategy.BuildScript("1.6.0", LinuxTentacleUpgradeStrategy.DefaultMethodOrder);

        // Rendered .sh has the env-resolved path in STATUS_DIR assignment.
        rendered.ShouldContain("STATUS_DIR=\"/srv/test-isolated/state\"",
            customMessage: "BuildScript MUST substitute the env-resolved state dir into STATUS_DIR. " +
                          "If this fails: the {{STATE_DIR}} placeholder wasn't wired through Replace, OR the .sh dropped the assignment line.");

        // Hardcoded `/var/lib/squid-tentacle` literal MUST NOT remain in
        // the assignment position. The string still appears in COMMENTS
        // (which is fine — they're documentation of the default), but
        // the actual STATUS_DIR=... assignment must use the substituted value.
        rendered.ShouldNotContain("STATUS_DIR=\"/var/lib/squid-tentacle\"",
            customMessage: "stale hardcoded STATUS_DIR literal in the rendered .sh — the env override is no-op if this is present");
    }

    [Fact]
    public void BuildScript_StateDir_DefaultsToVarLibInRender()
    {
        // No env var set → render uses the literal default. Operators not
        // overriding see no behaviour change.
        var rendered = LinuxTentacleUpgradeStrategy.BuildScript("1.6.0", LinuxTentacleUpgradeStrategy.DefaultMethodOrder);

        rendered.ShouldContain("STATUS_DIR=\"/var/lib/squid-tentacle\"",
            customMessage: "default render MUST inject /var/lib/squid-tentacle — operators not-overriding see pre-PR behaviour");
    }

    // ── J.L.E.6: HealthcheckRetriesEnvVar pins ─────────────────────────────

    [Fact]
    public void HealthcheckRetriesEnvVar_ConstantNamePinned()
    {
        LinuxTentacleUpgradeStrategy.HealthcheckRetriesEnvVar
            .ShouldBe("SQUID_TARGET_LINUX_TENTACLE_HEALTHCHECK_RETRIES");
    }

    [Fact]
    public void ResolveHealthcheckRetries_NoEnvVar_ReturnsDefault90()
    {
        // 90 × 1s sleep = 90s wait window. Pre-J.L.E.6 was hardcoded 90;
        // pin to preserve operator-observable behaviour after the env-var
        // refactor.
        LinuxTentacleUpgradeStrategy.ResolveHealthcheckRetries().ShouldBe(90);
    }

    [Theory]
    [InlineData("1", 1)]                  // tests use this to bypass the wait
    [InlineData("180", 180)]              // 3min for slow agents
    [InlineData("3600", 3600)]
    [InlineData("  60  ", 60)]
    public void ResolveHealthcheckRetries_ValidValue_RoundTripsAsInteger(string envValue, int expected)
    {
        SystemEnvironment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.HealthcheckRetriesEnvVar, envValue);

        LinuxTentacleUpgradeStrategy.ResolveHealthcheckRetries().ShouldBe(expected);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("90s")]
    [InlineData("not-a-number")]
    [InlineData("")]
    public void ResolveHealthcheckRetries_InvalidValue_FallsBackToDefault90(string envValue)
    {
        SystemEnvironment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.HealthcheckRetriesEnvVar, envValue);

        // Operator-friendly: invalid values fall back with Serilog warning.
        // 0 specifically — passing 0 to seq would loop 0 times → healthz
        // never checked → silent SUCCESS even on dead agent. Default
        // protects against this.
        LinuxTentacleUpgradeStrategy.ResolveHealthcheckRetries().ShouldBe(90);
    }

    [Fact]
    public void BuildScript_HealthcheckRetriesPlaceholder_SubstitutedFromEnv()
    {
        SystemEnvironment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.HealthcheckRetriesEnvVar, "5");

        var rendered = LinuxTentacleUpgradeStrategy.BuildScript("1.6.0", LinuxTentacleUpgradeStrategy.DefaultMethodOrder);

        // Direct substitution: the placeholder gets the resolved value as
        // a numeric literal.
        rendered.ShouldContain("HEALTHCHECK_RETRIES=5",
            customMessage: "BuildScript MUST substitute the env-resolved retry count. " +
                          "If this fails: placeholder wasn't wired through Replace, OR .sh dropped the variable assignment.");

        // Healthcheck loop MUST reference the variable, not a hardcoded value.
        rendered.ShouldContain("seq 1 \"$HEALTHCHECK_RETRIES\"",
            customMessage: "healthcheck loop MUST use \\$HEALTHCHECK_RETRIES — if it's a fixed integer, operator override is no-op");
    }

    [Fact]
    public void BuildScript_HealthcheckRetries_DefaultsTo90InRender()
    {
        var rendered = LinuxTentacleUpgradeStrategy.BuildScript("1.6.0", LinuxTentacleUpgradeStrategy.DefaultMethodOrder);

        rendered.ShouldContain("HEALTHCHECK_RETRIES=90",
            customMessage: "default render MUST inject 90 — operators not-overriding see pre-J.L.E.6 90s wait window");
    }

    [Theory]
    [InlineData("1.4.0", "linux-x64", "https://github.com/SolarifyDev/Squid/releases/download/1.4.0/squid-tentacle-1.4.0-linux-x64.tar.gz")]
    [InlineData("1.4.0", "linux-arm64", "https://github.com/SolarifyDev/Squid/releases/download/1.4.0/squid-tentacle-1.4.0-linux-arm64.tar.gz")]
    [InlineData("2.0.0-beta.1", "linux-x64", "https://github.com/SolarifyDev/Squid/releases/download/2.0.0-beta.1/squid-tentacle-2.0.0-beta.1-linux-x64.tar.gz")]
    public void BuildDownloadUrl_DefaultsToGitHubReleasesPath(string version, string rid, string expected)
    {
        LinuxTentacleUpgradeStrategy.BuildDownloadUrl(version, rid).ShouldBe(expected);
    }

    [Theory]
    [InlineData("https://mirror.acme.internal/squid", "1.4.0", "linux-x64", "https://mirror.acme.internal/squid/1.4.0/squid-tentacle-1.4.0-linux-x64.tar.gz")]
    [InlineData("https://s3.example.com/squid-mirror/", "1.4.0", "linux-arm64", "https://s3.example.com/squid-mirror/1.4.0/squid-tentacle-1.4.0-linux-arm64.tar.gz")]
    public void BuildDownloadUrl_EnvOverride_RetargetsToOperatorMirror(string baseUrl, string version, string rid, string expected)
    {
        SystemEnvironment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.DownloadBaseUrlEnvVar, baseUrl);

        LinuxTentacleUpgradeStrategy.BuildDownloadUrl(version, rid).ShouldBe(expected);
    }

    // ========================================================================
    // J.L.E.12 — E13.h-Linux env-var → rendered .sh propagation
    //
    // BuildDownloadUrl_EnvOverride above pins the env-var → URL chain in
    // isolation. The full chain into the rendered .sh template is:
    //
    //   env var → ResolveDownloadBaseUrl → BuildDownloadUrl → BuildScript
    //          → template substitution → rendered DOWNLOAD_URL= line
    //
    // If ANY link breaks, air-gap operators silently see github.com URLs
    // in the rendered script even with the env var set — the upgrade
    // hits the public mirror (DNS / connectivity fail) and surfaces as
    // exit-6 download failure instead of using the configured mirror.
    //
    // This test pins the FULL CHAIN. If a refactor splits BuildScript
    // from BuildDownloadUrl (e.g. caches the URL too eagerly, or uses
    // a different env-var path), this test fires immediately with a
    // clear "rendered script doesn't honour env override" message.
    //
    // Why this is unit-level not E2E: BuildScript hardcodes INSTALL_DIR /
    // SERVICE_NAME from production defaults, which would conflict with
    // E2E fixtures' GUID-suffixed paths. Test what we can at this layer
    // (the env-var → URL → template path); the existing E1.h-Linux E2E
    // tests the URL-substitution path via direct (non-env-var) injection.
    // ========================================================================

    [Fact]
    public void BuildScript_EnvOverride_RenderedDownloadUrlPointsAtOperatorMirror()
    {
        const string operatorMirror = "https://mirror.acme.internal/squid";
        const string version = "1.6.0";

        SystemEnvironment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.DownloadBaseUrlEnvVar, operatorMirror);

        var rendered = LinuxTentacleUpgradeStrategy.BuildScript(version);

        // Pin the FULL DOWNLOAD_URL= bash assignment line. Surgical match
        // (vs substring on URL alone) — the .sh has unrelated mentions
        // of github.com (firewall hint comment, apt-rollback URL) that
        // would noise a broader reverse-assert.
        //
        // The {RID} marker is the script's runtime arch placeholder
        // ($RID is the bash variable resolved from the `case "$ARCH"`
        // block at script start).
        var expectedDownloadUrlLine = $"DOWNLOAD_URL=\"{operatorMirror}/{version}/squid-tentacle-{version}-$RID.tar.gz\"";

        rendered.ShouldContain(expectedDownloadUrlLine,
            customMessage: $"rendered .sh MUST contain operator-mirror DOWNLOAD_URL line '{expectedDownloadUrlLine}'. " +
                          $"If absent: env-var → BuildScript chain regressed (most likely: BuildScript stopped going through " +
                          $"BuildDownloadUrl, OR ResolveDownloadBaseUrl was bypassed at one of the substitution sites). " +
                          $"Air-gap operator impact: every upgrade hits github.com instead of the configured mirror — exit 6 download fail.");

        // Reverse-assert: the active DOWNLOAD_URL line MUST NOT contain
        // github.com. Surgical: matches the assignment line pattern, not
        // any github.com mention anywhere in the script (comment + apt
        // rollback URL legitimately reference github.com).
        rendered.ShouldNotContain("DOWNLOAD_URL=\"https://github.com",
            customMessage: "rendered .sh's DOWNLOAD_URL= line contains github.com despite env override being set. " +
                          "BuildScript ignored the env override at the substitution site — air-gap operators would " +
                          "still hit github.com on every upgrade.");
    }

    [Fact]
    public void BuildScript_NoEnvOverride_RenderedDownloadUrlDefaultsToGitHub()
    {
        // Counterpart to the override test above: when the env var is
        // UNSET, the rendered DOWNLOAD_URL must default to the public
        // github.com release path. Without this dual pin, the override
        // test alone could pass while the default path silently broke
        // (e.g. someone hardcoded the operator mirror as the new default,
        // breaking every non-air-gap deployment).
        //
        // Default base URL pattern (per DefaultDownloadBaseUrl):
        //   https://github.com/SolarifyDev/Squid/releases/download
        // Resolved into:
        //   https://github.com/SolarifyDev/Squid/releases/download/<version>/squid-tentacle-<version>-$RID.tar.gz
        // (No `v<version>` tag prefix — the version is the path segment.)
        SystemEnvironment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.DownloadBaseUrlEnvVar, null);

        const string version = "1.6.0";
        var rendered = LinuxTentacleUpgradeStrategy.BuildScript(version);

        var expectedDownloadUrlLine = $"DOWNLOAD_URL=\"https://github.com/SolarifyDev/Squid/releases/download/{version}/squid-tentacle-{version}-$RID.tar.gz\"";

        rendered.ShouldContain(expectedDownloadUrlLine,
            customMessage: $"rendered .sh's default DOWNLOAD_URL line must be '{expectedDownloadUrlLine}' when no env override is set. " +
                          "If absent: the default base URL was changed without updating this test — confirm the change is intentional " +
                          "(not a copy-paste regression) and update the expected literal.");
    }

    [Fact]
    public void ResolveDownloadBaseUrl_StripsTrailingSlash_ToPreventDoubleSlashInUrl()
    {
        SystemEnvironment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.DownloadBaseUrlEnvVar, "https://mirror.acme.internal/path/");

        LinuxTentacleUpgradeStrategy.ResolveDownloadBaseUrl().ShouldBe("https://mirror.acme.internal/path");
    }

    [Theory]
    [InlineData("http://mirror.internal/squid")]
    [InlineData("HTTP://MIRROR.INTERNAL/squid")]   // case-insensitive scheme
    [InlineData("ftp://mirror.internal/squid")]   // even more wrong scheme
    public void ResolveDownloadBaseUrl_NonHttpsOverride_EmitsWarning_ButStillHonoursOperatorChoice(string override_)
    {
        // Round-4 audit B5: operators who set a non-HTTPS mirror have
        // likely made a mistake (copy-paste, forgot s) and we want the
        // security trail in logs. BUT — air-gapped internal HTTP mirrors
        // ARE a legitimate pattern, so we warn, not reject.
        var originalLogger = Serilog.Log.Logger;
        var sink = new CapturingLogSink();
        Serilog.Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        try
        {
            SystemEnvironment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.DownloadBaseUrlEnvVar, override_);

            var resolved = LinuxTentacleUpgradeStrategy.ResolveDownloadBaseUrl();

            resolved.ShouldBe(override_.TrimEnd('/'),
                "operator's choice must still be honoured (air-gap HTTP mirrors are legitimate)");
            sink.Events.ShouldContain(
                e => e.Level == Serilog.Events.LogEventLevel.Warning
                    && e.MessageTemplate.Text.Contains("non-HTTPS")
                    && e.MessageTemplate.Text.Contains("MITM"),
                customMessage: "must warn with MITM context so security review has a trail");
        }
        finally
        {
            Serilog.Log.Logger = originalLogger;
        }
    }

    [Fact]
    public void ResolveDownloadBaseUrl_HttpsOverride_NoWarningFired()
    {
        var originalLogger = Serilog.Log.Logger;
        var sink = new CapturingLogSink();
        Serilog.Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        try
        {
            SystemEnvironment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.DownloadBaseUrlEnvVar, "https://mirror.internal/squid");

            LinuxTentacleUpgradeStrategy.ResolveDownloadBaseUrl();

            sink.Events.ShouldNotContain(e => e.Level == Serilog.Events.LogEventLevel.Warning,
                "HTTPS override is the happy path — must not pollute ops dashboards with false-positive warnings");
        }
        finally
        {
            Serilog.Log.Logger = originalLogger;
        }
    }

    /// <summary>Minimal in-memory Serilog sink. Round-4 helper for pinning log contracts — mirror of the sink in MachineUpgradeServiceTests; kept local to avoid a test-project-wide helper that touches Serilog.Log.Logger across concurrent tests.</summary>
    private sealed class CapturingLogSink : Serilog.Core.ILogEventSink
    {
        public List<Serilog.Events.LogEvent> Events { get; } = new();

        public void Emit(Serilog.Events.LogEvent logEvent) => Events.Add(logEvent);
    }

    [Fact]
    public void ResolveDownloadBaseUrl_BlankOverride_FallsBackToDefault()
    {
        SystemEnvironment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.DownloadBaseUrlEnvVar, "   ");

        LinuxTentacleUpgradeStrategy.ResolveDownloadBaseUrl().ShouldBe("https://github.com/SolarifyDev/Squid/releases/download");
    }

    [Theory]
    [InlineData("TentaclePolling", true)]
    [InlineData("TentacleListening", true)]
    [InlineData("KubernetesAgent", false)]
    [InlineData("KubernetesApi", false)]
    [InlineData("Ssh", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CanHandle_OnlyMatchesLinuxTentacleStyles(string style, bool expected)
    {
        var strategy = new LinuxTentacleUpgradeStrategy(halibutClientFactory: null, observer: null);

        // empty capabilities (cold cache) preserves the
        //  behaviour where Linux strategy claimed all matching
        // styles regardless of OS. Linux-OS / Windows-OS routing is covered
        // by the dedicated OS-routing Theories below.
        strategy.CanHandle(style, MachineRuntimeCapabilities.Empty).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Linux", true)]    // explicit Linux — claims
    [InlineData("linux", true)]    // case-insensitive
    [InlineData("LINUX", true)]
    [InlineData("", true)]         // cold cache → IsUnknown=true → historical default (preserves behaviour)
    [InlineData("Unknown", true)]  // agent's "Unknown" fallback (when IsWindows/IsLinux/IsMacOS all false) routes to Linux historical default
    [InlineData("Windows", false)] // explicit Windows skip — WindowsTentacleUpgradeStrategy claims
    [InlineData("windows", false)]
    [InlineData("WINDOWS", false)]
    [InlineData("macOS", false)]   // explicit-claim refactor: Linux NO LONGER claims macOS (was true under "non-Windows" predicate). A future MacOSTentacleUpgradeStrategy plugs in WITHOUT MODIFYING THIS METHOD.
    [InlineData("MACOS", false)]   // case-insensitive
    [InlineData("FreeBSD", false)] // any unknown OS string (not in AgentOperatingSystems) → no Linux claim → resolver says "no strategy registered"; clear operator error vs silent install-tarball-on-FreeBSD attempt
    public void CanHandle_RoutesByOs_ClaimsLinuxOrUnknownOnly(string os, bool expected)
    {
        // explicit-claim semantic (was "non-Windows"
        // before): Linux strategy claims TentaclePolling/Listening for
        // agents whose OS is Linux OR Unknown (cold cache / agent's
        // "Unknown" fallback). It does NOT claim macOS / FreeBSD / any
        // other OS — those get a clean "no strategy registered" resolver
        // error until a dedicated strategy is added. The exactly-one-owner
        // invariant in MachineUpgradeService.ResolveStrategy depends on
        // this clean split.
        //
        // Key architectural property pinned: adding a future
        // MacOSTentacleUpgradeStrategy plugs in via DI auto-registration
        // WITHOUT MODIFYING THIS METHOD. The prior `!IsWindows` shape
        // would have force-claimed macOS, requiring Linux to add
        // `&& !IsMacOS` — open-closed violation.
        var strategy = new LinuxTentacleUpgradeStrategy(halibutClientFactory: null, observer: null);
        var capabilities = new MachineRuntimeCapabilities { Os = os };

        strategy.CanHandle(nameof(CommunicationStyle.TentaclePolling), capabilities).ShouldBe(expected);
    }

    [Fact]
    public void CanHandle_NullCapabilities_TreatedAsEmpty()
    {
        // Defensive: a future caller passing null capabilities (instead of
        // MachineRuntimeCapabilities.Empty) should not NPE. Same fallback
        // as the empty-OS case — Linux strategy claims.
        var strategy = new LinuxTentacleUpgradeStrategy(halibutClientFactory: null, observer: null);

        strategy.CanHandle(nameof(CommunicationStyle.TentaclePolling), capabilities: null).ShouldBeTrue();
    }

    // ========================================================================
    // BuildScript template safety (audit H-11) — without these tests, breaking
    // a placeholder name ships a script with literal {{INSTALL_DIR}} in `cd`,
    // which only fails when an upgrade runs against a real agent. These keep
    // the feedback loop on the unit-test side.
    // ========================================================================

    [Fact]
    public void BuildScript_AllPlaceholdersSubstituted_NoStaleTokensLeft()
    {
        var script = LinuxTentacleUpgradeStrategy.BuildScript("1.4.0");

        // Per-placeholder anchors so the failure message names the broken one.
        script.ShouldNotContain("{{TARGET_VERSION}}");
        script.ShouldNotContain("{{DOWNLOAD_URL}}");
        script.ShouldNotContain("{{EXPECTED_SHA256}}");
        script.ShouldNotContain("{{INSTALL_DIR}}");
        script.ShouldNotContain("{{SERVICE_NAME}}");
        script.ShouldNotContain("{{SERVICE_USER}}");

        // Catch-all: any `{{ALL_CAPS_TOKEN}}` left in the script is a
        // stale placeholder. The script's header comment contains the
        // literal sequence `{{...}}` (three dots) as documentation about
        // the placeholder convention — that's NOT a placeholder, so the
        // ALL-CAPS regex skips it cleanly.
        var stalePlaceholderPattern = new System.Text.RegularExpressions.Regex(@"\{\{[A-Z_]+\}\}");
        stalePlaceholderPattern.IsMatch(script).ShouldBeFalse(
            customMessage: "an unsubstituted '{{ALL_CAPS}}' placeholder ships a broken script — add a per-placeholder check above when introducing new placeholders");
    }

    [Fact]
    public void BuildScript_TargetVersionInjected_AppearsExactlyAsSet()
    {
        // Round-trips verbatim — strict semver gate at the service boundary
        // (see SemVer / MachineUpgradeService) is what makes this safe.
        var script = LinuxTentacleUpgradeStrategy.BuildScript("1.4.2");

        script.ShouldContain("TARGET_VERSION=\"1.4.2\"");
    }

    [Fact]
    public void BuildScript_DownloadUrlContainsBashRidVariable_NotServerSidePlaceholder()
    {
        // The server emits `$RID` (bash variable) inside the URL because the
        // architecture is detected on the agent side. If the literal `{RID}`
        // leaks through, the agent would try to download
        // squid-tentacle-1.4.0-{RID}.tar.gz — guaranteed 404.
        var script = LinuxTentacleUpgradeStrategy.BuildScript("1.4.0");

        script.ShouldContain("$RID");
        script.ShouldNotContain("{RID}");
    }

    [Fact]
    public void HealthcheckUrlEnvVar_ConstantNamePinned()
    {
        // Documented in design doc §6.5 operator-setup. Renaming breaks
        // every operator who customised the healthcheck URL (custom build,
        // non-standard port). Hard-pin. Audit H-14.
        LinuxTentacleUpgradeStrategy.HealthcheckUrlEnvVar.ShouldBe("SQUID_TARGET_LINUX_TENTACLE_HEALTHCHECK_URL");
    }

    [Fact]
    public void ResolveHealthcheckUrl_NoOverride_ReturnsDefault()
    {
        LinuxTentacleUpgradeStrategy.ResolveHealthcheckUrl().ShouldBe("http://127.0.0.1:8080/healthz");
    }

    [Theory]
    [InlineData("http://127.0.0.1:9090/api/health", "http://127.0.0.1:9090/api/health")]
    [InlineData("http://127.0.0.1:8080/custom/", "http://127.0.0.1:8080/custom")]   // trailing slash stripped
    [InlineData("  http://127.0.0.1:8080/healthz  ", "http://127.0.0.1:8080/healthz")]   // trim whitespace
    public void ResolveHealthcheckUrl_WithOverride_NormalisesAndReturns(string overrideValue, string expected)
    {
        SystemEnvironment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.HealthcheckUrlEnvVar, overrideValue);

        LinuxTentacleUpgradeStrategy.ResolveHealthcheckUrl().ShouldBe(expected);
    }

    [Fact]
    public void ResolveHealthcheckUrl_BlankOverride_FallsBackToDefault()
    {
        SystemEnvironment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.HealthcheckUrlEnvVar, "   ");

        LinuxTentacleUpgradeStrategy.ResolveHealthcheckUrl().ShouldBe("http://127.0.0.1:8080/healthz");
    }

    [Fact]
    public void BuildScript_EmbedsDefaultHealthcheckUrl_WhenNoOverride()
    {
        var script = LinuxTentacleUpgradeStrategy.BuildScript("1.4.0");

        script.ShouldContain("http://127.0.0.1:8080/healthz",
            customMessage: "default healthcheck URL must be baked into the script when no env override is set");
    }

    [Fact]
    public void BuildScript_EmbedsOverrideHealthcheckUrl_WhenEnvSet()
    {
        SystemEnvironment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.HealthcheckUrlEnvVar, "http://127.0.0.1:9090/api/health");

        var script = LinuxTentacleUpgradeStrategy.BuildScript("1.4.0");

        script.ShouldContain("http://127.0.0.1:9090/api/health");
        script.ShouldNotContain("http://127.0.0.1:8080/healthz",
            customMessage: "override must COMPLETELY replace the default, not coexist with it");
    }

    // ========================================================================
    // StartScriptCommand shape assertions — audit H-6 (ticket ID truncation
    // cargo-cult) + general "what actually goes on the Halibut wire" pinning.
    // ========================================================================

    [Fact]
    public void StartScriptCommand_TicketIdIncludesFullGuidEntropy_NotTruncatedTo32Chars()
    {
        // Audit H-6: old code truncated ticket ID to 32 chars, which for a
        // machine.Id=1000000000 left only 13 hex chars of GUID entropy
        // (2^52 collision space). Pin the full GUID is present.
        var cmd = LinuxTentacleUpgradeStrategy.PreviewStartScriptCommand(
            new Machine { Id = 1000000000, Name = "big-id", Endpoint = "{}" },
            "1.4.0");

        cmd.ScriptTicket.TaskId.Length.ShouldBeGreaterThan(40,
            "ticket ID must include full GUID + machine id + prefix, not be truncated");
        cmd.ScriptTicket.TaskId.ShouldStartWith("upgrade-1000000000-");
        // Everything after the machine-id prefix should be the full 32-hex GUID.
        var hexPart = cmd.ScriptTicket.TaskId["upgrade-1000000000-".Length..];
        hexPart.Length.ShouldBe(32);
        hexPart.ShouldMatch("^[0-9a-f]{32}$");
    }

    [Fact]
    public void StartScriptCommand_IsolationAndSyntaxPinned_FullIsolationBash()
    {
        // Isolation.FullIsolation is what lets the agent's ScriptIsolationMutex
        // serialize upgrades behind active deployments. A regression to
        // NoIsolation would dispatch the restart mid-deploy and break running
        // production jobs. Pin both values explicitly.
        var cmd = LinuxTentacleUpgradeStrategy.PreviewStartScriptCommand(
            new Machine { Id = 7, Name = "m", Endpoint = "{}" },
            "1.4.0");

        cmd.Isolation.ShouldBe(ScriptIsolationLevel.FullIsolation);
        cmd.ScriptSyntax.ShouldBe(ScriptType.Bash);
    }

    [Fact]
    public void StartScriptCommand_TwoInvocationsForSameMachine_ProduceDifferentTickets()
    {
        // Each upgrade request gets a fresh GUID → distinct ticket. Without
        // this, a redelivery (server retries a dropped RPC) would collide
        // on the same ticket and Halibut would believe the prior script was
        // still the same. The bash-side flock is the deeper idempotency;
        // per-invocation ticket is belt-and-braces.
        var m = new Machine { Id = 7, Name = "m", Endpoint = "{}" };

        var a = LinuxTentacleUpgradeStrategy.PreviewStartScriptCommand(m, "1.4.0");
        var b = LinuxTentacleUpgradeStrategy.PreviewStartScriptCommand(m, "1.4.0");

        a.ScriptTicket.TaskId.ShouldNotBe(b.ScriptTicket.TaskId);
    }

    [Fact]
    public void BuildScript_ExpectedSha256_DefaultsToEmptyUntilReleasePipelineEmitsHashes()
    {
        // : SHA256 placeholder is always empty (release pipeline doesn't
        // publish per-tarball hashes yet). The bash script treats empty as
        // "skip verification" — when lands, the test will be updated.
        var script = LinuxTentacleUpgradeStrategy.BuildScript("1.4.0");

        script.ShouldContain("EXPECTED_SHA256=\"\"");
    }

    // ========================================================================
    // Halibut dispatch branches (audit H-10) — uses mocked client factory +
    // observer to drive every outcome path. The integration / E2E suites
    // promised in design doc §8 will reuse the same shape against a real
    // TentacleStub.
    // ========================================================================

    private static Machine MakeMachine(int id, string style, string subscriptionId, string thumbprint)
    {
        var endpoint = $"{{\"CommunicationStyle\":\"{style}\",\"SubscriptionId\":\"{subscriptionId}\",\"Thumbprint\":\"{thumbprint}\"}}";

        return new Machine { Id = id, Name = $"machine-{id}", Endpoint = endpoint, SpaceId = 1 };
    }

    private static ScriptStatusResponse FakeStartResponse()
        => new(new ScriptTicket("t"), ProcessState.Pending, exitCode: 0, logs: new List<ProcessOutput>(), nextLogSequence: 0);

    [Fact]
    public async Task UpgradeAsync_MachineWithUnparseableEndpoint_ReturnsFailedWithoutDispatch()
    {
        // Endpoint missing SubscriptionId+Thumbprint → ParseHalibutEndpoint
        // returns null. We must bail BEFORE invoking the Halibut factory
        // (which would NPE on a null endpoint).
        var halibut = new Mock<IHalibutClientFactory>(MockBehavior.Strict);
        var observer = new Mock<IHalibutScriptObserver>(MockBehavior.Strict);
        var strategy = new LinuxTentacleUpgradeStrategy(halibut.Object, observer.Object);
        var brokenMachine = new Machine { Id = 1, Name = "broken", Endpoint = "{}", SpaceId = 1 };

        var outcome = await strategy.UpgradeAsync(brokenMachine, "1.4.0", CancellationToken.None);

        outcome.Status.ShouldBe(MachineUpgradeStatus.Failed);
        outcome.Detail.ShouldContain("Halibut endpoint");
        halibut.VerifyNoOtherCalls();
        observer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpgradeAsync_NullMachine_ReturnsFailed()
    {
        var strategy = new LinuxTentacleUpgradeStrategy(halibutClientFactory: null, observer: null);

        var outcome = await strategy.UpgradeAsync(machine: null, targetVersion: "1.4.0", CancellationToken.None);

        outcome.Status.ShouldBe(MachineUpgradeStatus.Failed);
    }

    [Fact]
    public async Task UpgradeAsync_BlankTargetVersion_ReturnsFailed()
    {
        var strategy = new LinuxTentacleUpgradeStrategy(halibutClientFactory: null, observer: null);
        var machine = MakeMachine(1, "TentaclePolling", "sub-1", "AABB");

        var outcome = await strategy.UpgradeAsync(machine, targetVersion: "  ", CancellationToken.None);

        outcome.Status.ShouldBe(MachineUpgradeStatus.Failed);
        outcome.Detail.ShouldContain("targetVersion");
    }

    [Fact]
    public async Task UpgradeAsync_ScriptSuccess_ReturnsUpgradedWithLogLineCount()
    {
        var (strategy, _, observer) = BuildMockedStrategy();
        var machine = MakeMachine(2, "TentaclePolling", "sub-2", "AABB");

        observer
            .Setup(o => o.ObserveAndCompleteAsync(It.IsAny<Machine>(), It.IsAny<IAsyncScriptService>(), It.IsAny<ScriptTicket>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<ScriptStatusResponse>(), It.IsAny<ServiceEndPoint>()))
            .ReturnsAsync(new ScriptExecutionResult { Success = true, LogLines = new List<string> { "downloading…", "swapping…", "✓ done" }, ExitCode = 0 });

        var outcome = await strategy.UpgradeAsync(machine, "1.4.2", CancellationToken.None);

        outcome.Status.ShouldBe(MachineUpgradeStatus.Upgraded);
        outcome.Detail.ShouldContain("1.4.2");
        outcome.Detail.ShouldContain("3 log lines");
        outcome.AgentVersionMayHaveChanged.ShouldBeTrue("script success → binary swap committed → cache must refresh (audit N-6)");
    }

    [Fact]
    public async Task UpgradeAsync_ScriptNonZeroExit_ReturnsFailedWithLastLogLine()
    {
        var (strategy, _, observer) = BuildMockedStrategy();
        var machine = MakeMachine(3, "TentaclePolling", "sub-3", "AABB");

        observer
            .Setup(o => o.ObserveAndCompleteAsync(It.IsAny<Machine>(), It.IsAny<IAsyncScriptService>(), It.IsAny<ScriptTicket>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<ScriptStatusResponse>(), It.IsAny<ServiceEndPoint>()))
            .ReturnsAsync(new ScriptExecutionResult { Success = false, ExitCode = 7, LogLines = new List<string> { "Downloading…", "::error:: SHA256 mismatch. Expected ABC, got DEF" } });

        var outcome = await strategy.UpgradeAsync(machine, "1.4.2", CancellationToken.None);

        outcome.Status.ShouldBe(MachineUpgradeStatus.Failed);
        outcome.Detail.ShouldContain("exit 7");
        outcome.Detail.ShouldContain("SHA256 mismatch");
        outcome.AgentVersionMayHaveChanged.ShouldBeFalse(
            "bash script rolls back on any post-swap failure → agent still on old binary → no need to invalidate cache");
    }

    [Fact]
    public async Task UpgradeAsync_ScriptFailedWithEmptyLogs_DoesNotIndexExceptionOnLastLog()
    {
        // Guard for the `LogLines[^1]` pattern — empty list would IndexOutOfRange
        // without the explicit count check.
        var (strategy, _, observer) = BuildMockedStrategy();
        var machine = MakeMachine(4, "TentaclePolling", "sub-4", "AABB");

        observer
            .Setup(o => o.ObserveAndCompleteAsync(It.IsAny<Machine>(), It.IsAny<IAsyncScriptService>(), It.IsAny<ScriptTicket>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<ScriptStatusResponse>(), It.IsAny<ServiceEndPoint>()))
            .ReturnsAsync(new ScriptExecutionResult { Success = false, ExitCode = 9, LogLines = new List<string>() });

        var outcome = await strategy.UpgradeAsync(machine, "1.4.2", CancellationToken.None);

        outcome.Status.ShouldBe(MachineUpgradeStatus.Failed);
        outcome.Detail.ShouldContain("(no log lines)");
        outcome.AgentVersionMayHaveChanged.ShouldBeFalse("script failure with rollback → cache valid");
    }

    [Fact]
    public async Task UpgradeAsync_HalibutDisconnectAfterDispatch_TreatedAsInitiatedNotFailed()
    {
        // ② Mid-script disconnect: StartScriptAsync HAS returned (script is
        // queued on agent), then the agent's `systemctl restart` kills the
        // polling RPC. The upgrade has almost certainly completed; the next
        // health check confirms the new version. Surfacing as Failed would
        // be a false negative on the success path.
        var (strategy, _, observer) = BuildMockedStrategy();
        var machine = MakeMachine(5, "TentaclePolling", "sub-5", "AABB");

        observer
            .Setup(o => o.ObserveAndCompleteAsync(It.IsAny<Machine>(), It.IsAny<IAsyncScriptService>(), It.IsAny<ScriptTicket>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<ScriptStatusResponse>(), It.IsAny<ServiceEndPoint>()))
            .ThrowsAsync(new HalibutClientException("connection closed"));

        var outcome = await strategy.UpgradeAsync(machine, "1.4.2", CancellationToken.None);

        outcome.Status.ShouldBe(MachineUpgradeStatus.Initiated);
        outcome.Detail.ShouldContain("disconnect");
        outcome.AgentVersionMayHaveChanged.ShouldBeTrue(
            "mid-script disconnect = script reached restart phase → version most likely changed → cache must refresh");
    }

    [Fact]
    public async Task UpgradeAsync_LivenessProbeTripsAfterDispatch_TreatedAsInitiatedNotFailed()
    {
        // ②' Liveness probe abort is the SAME "agent disconnected mid-script
        // because it restarted" scenario as HalibutClientException, but
        // surfaced through HalibutScriptObserver's independent probe loop
        // (AgentUnreachableException) instead of a GetStatus/Complete RPC
        // failure. Before this mapping, the probe abort fell through to
        // the generic Exception catch and surfaced as "Failed:
        // AgentUnreachableException" — telling the UI the upgrade failed
        // even though the scoped script was mid-flight and about to
        // complete. Operators saw Failed in the UI while the agent
        // silently succeeded. Must map to Initiated like the Halibut
        // mid-script path.
        var (strategy, _, observer) = BuildMockedStrategy();
        var machine = MakeMachine(25, "TentaclePolling", "sub-25", "AABB");

        observer
            .Setup(o => o.ObserveAndCompleteAsync(It.IsAny<Machine>(), It.IsAny<IAsyncScriptService>(), It.IsAny<ScriptTicket>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<ScriptStatusResponse>(), It.IsAny<ServiceEndPoint>()))
            .ThrowsAsync(new AgentUnreachableException("mars mac", consecutiveFailures: 2));

        var outcome = await strategy.UpgradeAsync(machine, "1.5.5", CancellationToken.None);

        outcome.Status.ShouldBe(MachineUpgradeStatus.Initiated,
            "liveness probe mid-restart = script reached restart phase → Initiated, NOT Failed");
        outcome.Detail.ShouldContain("liveness",
            customMessage: "detail must name the probe mechanism so operators understand why the observer aborted");
        outcome.Detail.ShouldContain("2",
            customMessage: "detail must include the consecutive-failure count from the exception");
        outcome.AgentVersionMayHaveChanged.ShouldBeTrue(
            "probe abort = script reached restart phase → version most likely changed → cache must refresh");
    }

    [Fact]
    public async Task UpgradeAsync_HalibutDisconnectBeforeDispatchAcked_ReturnsFailedNotInitiated()
    {
        // ① Pre-dispatch failure (audit N-1): StartScriptAsync threw —
        // agent unreachable, TLS handshake refused, TCP reset on connect.
        // The script was NEVER queued. Telling the operator "Initiated, check
        // next health check" is misleading: the next health check finds NO
        // state change because nothing ran. Must surface as Failed with a
        // message that names the dispatch step so the operator looks at
        // network/cert/agent-up status, not at the agent's logs.
        var halibut = new Mock<IHalibutClientFactory>();
        var observer = new Mock<IHalibutScriptObserver>(MockBehavior.Strict);
        var scriptClient = new Mock<IAsyncScriptService>();

        scriptClient
            .Setup(s => s.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .ThrowsAsync(new HalibutClientException("Polling endpoint not registered (TLS handshake failed)"));

        halibut
            .Setup(f => f.CreateClient(It.IsAny<ServiceEndPoint>()))
            .Returns(scriptClient.Object);

        var strategy = new LinuxTentacleUpgradeStrategy(halibut.Object, observer.Object);
        var machine = MakeMachine(15, "TentaclePolling", "sub-15", "AABB");

        var outcome = await strategy.UpgradeAsync(machine, "1.4.2", CancellationToken.None);

        outcome.Status.ShouldBe(MachineUpgradeStatus.Failed,
            "StartScriptAsync threw → script was NEVER dispatched → must NOT report Initiated");
        outcome.Detail.ShouldContain("dispatch",
            customMessage: "operator must understand the failure was at the dispatch step (network/cert/agent-down), not mid-script");
        outcome.AgentVersionMayHaveChanged.ShouldBeFalse(
            "pre-dispatch failure = script never reached agent → binary unchanged → no invalidation needed");
        observer.VerifyNoOtherCalls();   // observer must NOT be called when dispatch failed
    }

    [Fact]
    public async Task UpgradeAsync_OperationCancelledAtStartScript_RethrowsInsteadOfFalseFailed()
    {
        // Symmetry with N-1: a cancellation at the dispatch step must also
        // propagate (not be eaten by the broad catch-all that maps to Failed).
        var halibut = new Mock<IHalibutClientFactory>();
        var observer = new Mock<IHalibutScriptObserver>(MockBehavior.Strict);
        var scriptClient = new Mock<IAsyncScriptService>();

        scriptClient
            .Setup(s => s.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .ThrowsAsync(new OperationCanceledException());

        halibut
            .Setup(f => f.CreateClient(It.IsAny<ServiceEndPoint>()))
            .Returns(scriptClient.Object);

        var strategy = new LinuxTentacleUpgradeStrategy(halibut.Object, observer.Object);
        var machine = MakeMachine(16, "TentaclePolling", "sub-16", "AABB");

        await Should.ThrowAsync<OperationCanceledException>(() =>
            strategy.UpgradeAsync(machine, "1.4.2", CancellationToken.None));
    }

    [Fact]
    public async Task UpgradeAsync_GenericExceptionAtStartScript_ReturnsFailed()
    {
        // A non-Halibut, non-cancellation exception at StartScriptAsync
        // (e.g. some DI-resolution problem inside the factory) hits the
        // generic catch-all. Detail must include the type so the operator
        // can route to "look at server logs" not "look at agent".
        var halibut = new Mock<IHalibutClientFactory>();
        var observer = new Mock<IHalibutScriptObserver>(MockBehavior.Strict);
        var scriptClient = new Mock<IAsyncScriptService>();

        scriptClient
            .Setup(s => s.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .ThrowsAsync(new InvalidOperationException("DI scope disposed"));

        halibut
            .Setup(f => f.CreateClient(It.IsAny<ServiceEndPoint>()))
            .Returns(scriptClient.Object);

        var strategy = new LinuxTentacleUpgradeStrategy(halibut.Object, observer.Object);
        var machine = MakeMachine(17, "TentaclePolling", "sub-17", "AABB");

        var outcome = await strategy.UpgradeAsync(machine, "1.4.2", CancellationToken.None);

        outcome.Status.ShouldBe(MachineUpgradeStatus.Failed);
        outcome.Detail.ShouldContain("InvalidOperationException");
        outcome.Detail.ShouldContain("DI scope disposed");
        outcome.AgentVersionMayHaveChanged.ShouldBeFalse("generic failure at dispatch → agent untouched");
        observer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpgradeAsync_OperationCancelled_RethrowsInsteadOfSwallowing()
    {
        // CancellationToken-driven shutdown (server SIGTERM, request abort)
        // must propagate so callers can clean up; eating it would convert
        // a deliberate cancellation into a fake "Failed" status.
        var (strategy, _, observer) = BuildMockedStrategy();
        var machine = MakeMachine(6, "TentaclePolling", "sub-6", "AABB");

        observer
            .Setup(o => o.ObserveAndCompleteAsync(It.IsAny<Machine>(), It.IsAny<IAsyncScriptService>(), It.IsAny<ScriptTicket>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<ScriptStatusResponse>(), It.IsAny<ServiceEndPoint>()))
            .ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(() =>
            strategy.UpgradeAsync(machine, "1.4.2", CancellationToken.None));
    }

    [Fact]
    public async Task UpgradeAsync_UnexpectedException_ReturnsFailedWithExceptionTypeAndMessage()
    {
        // Catch-all for the unknown-unknowns. We don't crash the orchestrator —
        // the operator gets a typed message they can act on.
        var (strategy, _, observer) = BuildMockedStrategy();
        var machine = MakeMachine(7, "TentaclePolling", "sub-7", "AABB");

        observer
            .Setup(o => o.ObserveAndCompleteAsync(It.IsAny<Machine>(), It.IsAny<IAsyncScriptService>(), It.IsAny<ScriptTicket>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<ScriptStatusResponse>(), It.IsAny<ServiceEndPoint>()))
            .ThrowsAsync(new InvalidOperationException("hash provider exploded"));

        var outcome = await strategy.UpgradeAsync(machine, "1.4.2", CancellationToken.None);

        outcome.Status.ShouldBe(MachineUpgradeStatus.Failed);
        outcome.Detail.ShouldContain("InvalidOperationException");
        outcome.Detail.ShouldContain("hash provider exploded");
    }

    private static (LinuxTentacleUpgradeStrategy strategy, Mock<IHalibutClientFactory> halibut, Mock<IHalibutScriptObserver> observer) BuildMockedStrategy()
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

        return (new LinuxTentacleUpgradeStrategy(halibut.Object, observer.Object), halibut, observer);
    }
}
