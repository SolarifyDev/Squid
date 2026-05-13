namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// xUnit Trait categories for the Windows upgrade E2E
/// suite. Mirrors the discipline in Squid.Tentacle.Tests.Support.TentacleTestCategories.
///
/// <para>The CI workflow filters by these categories so a developer running
/// the suite locally on macOS/Linux gets a clean "0 ran, 0 passed" rather
/// than spurious failures from `if (!OperatingSystem.IsWindows()) return`
/// short-circuits.</para>
/// </summary>
public static class WindowsUpgradeE2ECategories
{
    /// <summary>
    /// E2E coverage for the <c>WindowsTentacleUpgradeStrategy.BuildOuterWrapper</c>
    /// + Task Scheduler one-shot detach mechanism. Runs the wrapper PowerShell
    /// against a real Windows host, verifies <c>schtasks</c> registers + runs
    /// + auto-deletes the detached task, and that the inner script executes
    /// in a SYSTEM-identity process tree separate from the wrapper's own.
    /// </summary>
    public const string Wrapper = "WindowsUpgradeWrapperE2E";

    /// <summary>
    /// E2E coverage for the Phase B physical mechanics
    /// (Stop-Service / Move-Item swap / Start-Service / version verify)
    /// against a real <c>sc.exe</c>-installed Windows service. Uses
    /// <c>WindowsServiceFixture</c> to install + start + cleanup the
    /// minimal <c>SquidUpgradeE2ETestService</c> binary so the upgrade
    /// pipeline's filesystem-swap logic runs against a real running
    /// service, not a mock.
    /// </summary>
    public const string Service = "WindowsUpgradeServiceE2E";

    /// <summary>
    /// E2E coverage for the opportunistic SHA256
    /// companion-file fetch + verification logic in
    /// <c>upgrade-windows-tentacle.ps1</c>. Uses an in-process HTTP
    /// listener serving a known .sha256 + .zip pair so the .ps1's
    /// <c>Invoke-WebRequest</c> + <c>Get-FileHash</c> path runs against
    /// a real local server (no GitHub Releases dependency for the test
    /// — the fixture controls EVERY response: 404, invalid body, valid
    /// match, valid mismatch).
    /// </summary>
    public const string ShaVerify = "WindowsUpgradeShaVerifyE2E";

    /// <summary>
    /// E2E coverage for the production
    /// <c>Squid.Tentacle.ServiceHost.WindowsServiceHost</c> class — the
    /// SCM lifecycle round-trip (Install → Start → Stop → Uninstall) the
    /// upgrade pipeline depends on. Unit tests pin the sc.exe argv shape;
    /// this category proves the SAME argv actually produces a Running /
    /// Stopped / Absent service in the SCM database when executed against
    /// a real Windows host. Companion to the systemd E2E that lives
    /// alongside the Linux upgrade tests (Phase 12.F).
    /// </summary>
    public const string ServiceHost = "WindowsServiceHostE2E";

    /// <summary>
    /// Phase 12.H smoke tests for the
    /// <see cref="Infrastructure.StubSquidServer"/> shared fixture. Tier
    /// 🔵 Fixture-only (Rule 12) — does NOT count toward production E2E
    /// coverage. Subsequent Phase 12.I+ categories (register, deploy,
    /// upgrade) consume the stub and provide the high-fidelity
    /// production coverage.
    /// </summary>
    public const string StubSquidServer = "StubSquidServerE2E";

    /// <summary>
    /// Phase 12.I E2E coverage for the production
    /// <c>squid-tentacle register</c> CLI: handshake against
    /// <see cref="Infrastructure.StubSquidServer"/>'s REST endpoint,
    /// config-file persistence at <c>PlatformPaths.GetInstanceConfigPath</c>,
    /// and InstanceRegistry update. Tier 🟢 high-fidelity — drives
    /// <c>RegisterCommand.ExecuteAsync</c> directly with real HTTP +
    /// real JSON config write. Cross-platform (runs on macOS / Linux /
    /// Windows) — Squid.Tentacle's register flow is OS-agnostic except
    /// for the Linux ownership-handover step which is covered separately
    /// in the Linux phase.
    /// </summary>
    public const string TentacleRegister = "TentacleRegisterE2E";

    /// <summary>
    /// Phase 12.J E2E coverage for the deployment-execution round-trip:
    /// server (<see cref="Infrastructure.StubSquidServer"/>) →
    /// agent (<see cref="Infrastructure.StubAgent"/> wrapping the
    /// production <c>LocalScriptService</c>) → real shell execution
    /// (PowerShell on Windows / bash on Linux+macOS) → results back
    /// over Halibut RPC. Tier 🟢 high-fidelity — every component except
    /// the upstream Squid server is production code. Cross-platform
    /// (runs on Windows / Linux / macOS) via per-OS script-syntax
    /// branches in each test method.
    /// </summary>
    public const string TentacleDeploy = "TentacleDeployE2E";

    /// <summary>
    /// Phase 12.J.E E2E coverage for the production
    /// <c>WindowsTentacleUpgradeStrategy.UpgradeAsync</c> end-to-end:
    /// strategy constructs the outer wrapper, dispatches via Halibut to a
    /// real listening agent, observes via real <c>HalibutScriptObserver</c>,
    /// maps the script result to <c>MachineUpgradeOutcome</c>. Tier 🟢
    /// high-fidelity — every component is production code, only the
    /// Squid server is replaced by <see cref="Infrastructure.StubSquidServer"/>.
    ///
    /// <para>Coverage delta vs <c>WindowsUpgradeWrapperE2ETests</c>: that
    /// suite tests <c>BuildOuterWrapper</c> in isolation by running the
    /// returned PowerShell directly. This category adds the FULL
    /// dispatch+observe path through Halibut RPC, so a regression in
    /// <c>HalibutClientFactory</c>, <c>HalibutScriptObserver</c>, or the
    /// outcome mapper surfaces here.</para>
    /// </summary>
    public const string TentacleUpgrade = "TentacleUpgradeE2E";

    /// <summary>
    /// Phase 12.K E2E coverage for the production
    /// <c>deploy/scripts/install-tentacle.{sh,ps1}</c> install scripts.
    /// Drives the actual scripts via real <c>powershell.exe</c> / <c>bash</c>
    /// against a <see cref="Infrastructure.LocalReleaseMirror"/> serving
    /// fake zip / tarball downloads. Tier 🟢 high-fidelity — every
    /// component except the upstream GitHub Releases CDN is real
    /// (real shell, real script, real Expand-Archive / tar xzf, real
    /// install-dir IO).
    ///
    /// <para><b>OS scope</b>: install-tentacle.ps1 is Windows-only (uses
    /// <c>Expand-Archive</c>, <c>New-NetFirewallRule</c>); install-tentacle.sh
    /// is Linux-only (uses <c>apt</c>/<c>dnf</c>/<c>useradd</c>/<c>tar</c>).
    /// Each test method skip-guards on its target OS.</para>
    /// </summary>
    public const string TentacleInstallScript = "TentacleInstallScriptE2E";

    /// <summary>
    /// Phase 12.L E2E coverage for multi-instance scenarios — two or
    /// more Windows services installed concurrently on the same SCM
    /// without collision; uninstalling one preserves the others.
    /// Tier 🟢 high-fidelity — drives real <see cref="Squid.Tentacle.ServiceHost.WindowsServiceHost"/>
    /// against real sc.exe. Windows-only (uses sc.exe).
    /// </summary>
    public const string TentacleMultiInstance = "TentacleMultiInstanceE2E";

    /// <summary>
    /// Phase 12.J.E.2 E2E coverage for the capabilities probe round-trip
    /// — the path the production server uses to read agent's reported
    /// version (post-deploy / post-upgrade cache refresh). Tier 🟢
    /// high-fidelity. Cross-platform — Halibut + capabilities service
    /// run on every OS without skip-guards.
    ///
    /// <para><b>Unblock history</b>: this category was blocked from
    /// Phase 12.J.E.2 first attempt by a Halibut 8.1 cache-key bug
    /// (every probe threw <c>ArgumentOutOfRangeException</c> before
    /// any RPC). Fixed by PR #194 — <see cref="CapabilitiesRequest"/>
    /// now implements <c>IEnumerable&lt;string&gt;</c> + <c>[JsonObject(OptIn)]</c>.</para>
    /// </summary>
    public const string TentacleCapabilities = "TentacleCapabilitiesE2E";

    /// <summary>
    /// Phase 12.J.E.3 E2E coverage for the FULL Windows upgrade lifecycle
    /// — drives the production <c>upgrade-windows-tentacle.ps1</c> end-to-
    /// end against a real <see cref="Infrastructure.LocalReleaseMirror"/>
    /// (zip + SHA256 companion) + a real
    /// <see cref="Infrastructure.WindowsServiceFixture"/>-installed Windows
    /// service. Verifies Phase A (download + SHA verify + extract) AND
    /// Phase B (Stop → swap → Start) AND <c>last-upgrade.json</c> writeback
    /// — the round-trip operator UI relies on for post-upgrade status.
    ///
    /// <para><b>Tier</b>: 🟢 High-fidelity (Rule 12). Production .ps1
    /// loaded from disk verbatim, real <c>Invoke-WebRequest</c> +
    /// <c>Get-FileHash</c> + <c>Expand-Archive</c> + <c>Stop-Service</c> +
    /// <c>Move-Item</c> + <c>Start-Service</c> — only the upstream GitHub
    /// Releases CDN is replaced (LocalReleaseMirror) and <c>$env:ProgramData</c>
    /// is redirected to a test-isolated dir so <c>last-upgrade.json</c> /
    /// <c>upgrade.lock</c> / <c>upgrade.log</c> writes don't pollute the
    /// host machine.</para>
    ///
    /// <para><b>Coverage delta vs <c>WindowsUpgradeServiceE2E</c>
    /// (PhaseB)</b>: that category exercises only the Phase B mechanics
    /// (Stop / Move-Item / Start) via an inline mirror with a drift
    /// detector — medium-fidelity. This category exercises the full Phase
    /// A + Phase B template against a real running service, covering the
    /// HTTP download, SHA256 fetch + verify, archive extract, AND swap +
    /// restart sequence, AND asserts on the resulting
    /// <c>last-upgrade.json</c> payload (the operator-visible outcome).
    /// Catches regressions invisible to PhaseB tests: download URL
    /// construction, SHA companion fetch, Expand-Archive layout
    /// assumptions, status JSON schema.</para>
    ///
    /// <para><b>Windows-only</b>: uses sc.exe-installed service +
    /// PowerShell-only cmdlets (<c>Stop-Service</c>, <c>Start-Service</c>,
    /// <c>Expand-Archive</c>, <c>Get-FileHash</c>). Skip-guards on
    /// non-Windows dev hosts.</para>
    /// </summary>
    public const string TentacleUpgradeLifecycle = "TentacleUpgradeLifecycleE2E";

    /// <summary>
    /// Phase 12.M.W.D E2E coverage for the operator-tailed diagnostic
    /// commands (<c>show-thumbprint</c>, <c>list-instances</c>,
    /// <c>show-config</c>, <c>new-certificate</c>) on Windows — mirrors
    /// the Linux Section D pinned by
    /// <c>TentacleLinuxDiagnosticCommandE2ETests</c>.
    ///
    /// <para>Why this matters cross-platform: when an agent fails to poll,
    /// the operator's documented debug recipe is "run show-thumbprint /
    /// show-config / list-instances and verify against server-side state".
    /// If any of these lie or break silently on Windows, the trust-list
    /// debug experience differs from Linux and confuses every operator
    /// running mixed fleets. Tier 🟢 high-fidelity — drives the
    /// production command classes (<c>ShowThumbprintCommand.ExecuteAsync</c>
    /// etc.) directly with real cert manager + real config IO + real
    /// stub HTTP exchange (D1h round-trip).</para>
    /// </summary>
    public const string TentacleDiagnostic = "TentacleDiagnosticE2E";

    /// <summary>
    /// Phase 13 PR-2+ E2E coverage for the real production
    /// <c>Squid.Tentacle.exe</c> binary's CLI surface — Windows mirror
    /// of <c>Squid.LinuxTentacleE2ETests.LinuxTentacleE2ECategories.TentacleBinary</c>.
    ///
    /// <para>Tier 🟢 high-fidelity — drives the actual published binary
    /// (built via <c>dotnet publish -r win-x64 --self-contained</c>
    /// matching production CI). UNBLOCKS Phase 13 PR-3 (real binary as
    /// polling agent — the highest-fidelity Windows E2E).</para>
    ///
    /// <para>Windows-only; the fixture's binary is a self-contained
    /// <c>win-x64</c> bundle that won't run on macOS / Linux.</para>
    /// </summary>
    public const string TentacleBinary = "WindowsTentacleBinaryE2E";

    /// <summary>
    /// E2E coverage for the production
    /// <c>Squid.DeployToIISWebSite</c> action's PowerShell payload against
    /// a real Windows host with real IIS installed. Drives
    /// <c>IISDeployScriptBuilder.Build(action)</c> to produce the same
    /// script the dispatch path would ship, then executes it via real
    /// <c>powershell.exe</c> and verifies cluster-equivalent state via
    /// <c>Get-Website</c> / <c>Get-WebAppPool</c> / <c>Get-WebBinding</c>.
    ///
    /// <para><b>Tier</b>: 🟢 High-fidelity (Rule 12). Real IIS, real
    /// PowerShell, real <c>WebAdministration</c> module, real metabase
    /// state. The only seam vs an end-to-end deploy from a Squid server
    /// is the Halibut RPC layer between server and agent — that layer is
    /// covered by <see cref="TentacleDeploy"/> and is intentionally
    /// out-of-scope here so this suite can run on
    /// <c>windows-latest</c> without spinning up a server-side stack.</para>
    ///
    /// <para><b>Coverage delta vs the pipeline-tier
    /// <c>IISDeployPipelineE2ETests</c> in <c>Squid.E2ETests</c></b>: that
    /// category asserts the rendered <c>ScriptExecutionRequest</c> shape
    /// (medium-mock — <c>CapturingExecutionStrategy</c>); this category
    /// proves the script actually does the right thing to real IIS
    /// metabase state. Both ship in the same PR so a regression in
    /// either rendering OR runtime is caught.</para>
    ///
    /// <para>Windows-only — requires <c>Web-WebServer</c> Windows feature
    /// installed. CI workflow installs it before invoking the test step;
    /// local devs without IIS see a clean skip via
    /// <c>if (!OperatingSystem.IsWindows()) return;</c> + per-test
    /// <c>Get-WindowsFeature</c> probe.</para>
    /// </summary>
    public const string IISDeploy = "IISDeployE2E";
}
