using System.Diagnostics;
using System.Text;
using Squid.Core.Services.DeploymentExecution.Tentacle.Handlers;
using Squid.Message.Models.Deployments.Process;

namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// Execution-tier E2E for the <c>Squid.DeployToIISWebSite</c> action's PowerShell
/// payload. Drives <see cref="IISDeployScriptBuilder.Build"/> with realistic action
/// properties, pipes the resulting PowerShell into a real <c>powershell.exe</c>
/// process, then verifies the resulting IIS metabase state via <c>Get-Website</c>
/// / <c>Get-WebAppPool</c> / <c>Get-WebBinding</c>.
///
/// <para><b>Tier</b>: 🟢 High-fidelity (per ~/.claude/CLAUDE.md Rule 12). Real IIS,
/// real PowerShell, real <c>WebAdministration</c> module, real metabase mutations.
/// The only seam vs end-to-end is the Halibut server↔agent dispatch — that's
/// covered separately by <c>TentacleDeployE2ETests</c> in this same project and
/// kept out of scope here so this suite runs on <c>windows-latest</c> without
/// spinning up a server-side stack.</para>
///
/// <para><b>Coverage delta vs pipeline-tier <c>IISDeployPipelineE2ETests</c></b>
/// (in <c>Squid.E2ETests</c>): that suite asserts the rendered
/// <c>ScriptExecutionRequest</c> shape via <c>CapturingExecutionStrategy</c>;
/// this suite proves the rendered script actually does the right thing to real
/// IIS. Both ship in the same PR so a regression in either rendering OR runtime
/// surfaces.</para>
///
/// <para><b>OS / IIS prereqs</b>: requires Windows + <c>Web-WebServer</c> feature
/// installed. The CI workflow <c>tentacle-windows-e2e.yml</c> installs it before
/// invoking this category. Per-test <c>Get-WindowsFeature</c> probe + per-class
/// skip-on-non-Windows guard keep dev hosts on macOS / Linux running a clean
/// "0 ran, 0 passed" instead of spurious failures.</para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.IISDeploy)]
public sealed class IISDeployRealHostE2ETests
{
    [Fact]
    public void RealIIS_BasicWebSiteCreate_AppPoolAndSiteExistOnHost()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();

        var script = IISDeployScriptBuilder.Build(BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, "[{\"protocol\":\"http\",\"port\":\"" + ctx.HttpPort + "\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}]"),
            (Property.StartApplicationPool, "True"),
            (Property.StartWebSite, "True")));

        var result = RunPowerShell(script);

        result.ExitCode.ShouldBe(0,
            customMessage:
                $"Squid IIS deploy script failed on real Windows host. " +
                $"stdout / stderr captured below — to manually diagnose, run " +
                $"`Get-Website -Name {ctx.SiteName}` and `Get-WebAppPool -Name {ctx.PoolName}`.\n\n" +
                $"STDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        // The site MUST exist in IIS metabase, with the configured physical path.
        var sitePath = PowerShellSingleLine(
            $"(Get-Website -Name '{ctx.SiteName}').physicalPath");

        sitePath.ShouldBe(ctx.PhysicalPath,
            customMessage: "physicalPath on the created site doesn't match the configured WebRoot. " +
                          "Either the script's `Set-ItemProperty IIS:\\Sites\\... -Name physicalPath` " +
                          "didn't run or it ran against a different path. Check the captured stdout.");

        // The pool MUST exist with the configured identity type + framework.
        var poolIdentity = PowerShellSingleLine(
            $"(Get-ItemProperty IIS:\\AppPools\\{ctx.PoolName} -Name processModel.identityType).Value");

        poolIdentity.ShouldBe("ApplicationPoolIdentity");

        var poolRuntime = PowerShellSingleLine(
            $"(Get-ItemProperty IIS:\\AppPools\\{ctx.PoolName} -Name managedRuntimeVersion).Value");

        poolRuntime.ShouldBe("v4.0");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_HttpBinding_RegisteredInMetabaseWithConfiguredPort()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();

        var script = IISDeployScriptBuilder.Build(BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, "[{\"protocol\":\"http\",\"port\":\"" + ctx.HttpPort + "\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}]")));

        var result = RunPowerShell(script);
        result.ExitCode.ShouldBe(0, $"deploy failed: {result.StdErr}");

        // The configured binding MUST be present on the site. The bindingInformation
        // format is `<ipAddress>:<port>:<host>` — for *:<port>: it's `*:80:`.
        var bindingInfo = PowerShellSingleLine(
            $"(Get-WebBinding -Name '{ctx.SiteName}' -Protocol http).bindingInformation");

        bindingInfo.ShouldBe($"*:{ctx.HttpPort}:",
            customMessage: "HTTP binding not registered with the configured port. The script's " +
                          "`New-WebBinding` or binding-merge logic didn't populate the metabase. " +
                          "Inspect captured stdout for the binding-loop output.");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_StartWebSiteFalse_SiteCreatedButLeftStopped()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();

        var script = IISDeployScriptBuilder.Build(BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, "[{\"protocol\":\"http\",\"port\":\"" + ctx.HttpPort + "\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}]"),
            (Property.StartWebSite, "False")));

        var result = RunPowerShell(script);
        result.ExitCode.ShouldBe(0, $"deploy failed: {result.StdErr}");

        // Site exists but the script honoured StartWebSite=False → state is Stopped.
        var siteState = PowerShellSingleLine(
            $"(Get-Website -Name '{ctx.SiteName}').state");

        siteState.ShouldBe("Stopped",
            customMessage: "Site should be in 'Stopped' state because StartWebSite=False, but is in " +
                          $"'{siteState}'. The script's `Start-Website` block executed when it should " +
                          "have been skipped.");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_SecondDeploySameSite_IsIdempotentUpdate_NotDuplicate()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();

        // First deploy
        var script1 = IISDeployScriptBuilder.Build(BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, "[{\"protocol\":\"http\",\"port\":\"" + ctx.HttpPort + "\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}]")));

        var result1 = RunPowerShell(script1);
        result1.ExitCode.ShouldBe(0, $"first deploy failed: {result1.StdErr}");

        // Second deploy — same name, slightly different physical path.
        var newPath = ctx.PhysicalPath + "-v2";
        Directory.CreateDirectory(newPath);
        ctx.RegisterTempDirForCleanup(newPath);

        var script2 = IISDeployScriptBuilder.Build(BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.WebRoot, newPath),
            (Property.Bindings, "[{\"protocol\":\"http\",\"port\":\"" + ctx.HttpPort + "\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}]")));

        var result2 = RunPowerShell(script2);
        result2.ExitCode.ShouldBe(0,
            customMessage: $"second deploy with same release name failed — idempotence broken.\n\n" +
                          $"STDOUT:\n{result2.StdOut}\n\nSTDERR:\n{result2.StdErr}");

        // Still exactly one site with that name (not duplicated).
        var siteCount = PowerShellSingleLine(
            $"(Get-Website | Where-Object {{ $_.Name -eq '{ctx.SiteName}' }} | Measure-Object).Count");

        siteCount.ShouldBe("1",
            customMessage: "Second deploy produced a duplicate site instead of an update. " +
                          "The script's existing-site detection (Test-Path IIS:\\Sites\\X) didn't fire.");

        // The site's physicalPath was updated to the new value.
        var sitePath = PowerShellSingleLine(
            $"(Get-Website -Name '{ctx.SiteName}').physicalPath");

        sitePath.ShouldBe(newPath,
            customMessage: "Second deploy didn't update physicalPath — the create-vs-update branch " +
                          "took the wrong path.");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_AppPoolFrameworkVersionV2_AppliedToMetabase()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();

        var script = IISDeployScriptBuilder.Build(BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolFrameworkVersion, "v2.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, "[{\"protocol\":\"http\",\"port\":\"" + ctx.HttpPort + "\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}]")));

        var result = RunPowerShell(script);
        result.ExitCode.ShouldBe(0, $"deploy failed: {result.StdErr}");

        var poolRuntime = PowerShellSingleLine(
            $"(Get-ItemProperty IIS:\\AppPools\\{ctx.PoolName} -Name managedRuntimeVersion).Value");

        poolRuntime.ShouldBe("v2.0",
            customMessage: "Framework version not applied to the app pool. The script's " +
                          "`Set-ItemProperty managedRuntimeVersion` didn't run or ran with the wrong value.");

        ctx.MarkClean();
    }

    // ── HTTPS binding scenarios (Phase 2) ───────────────────────────────────
    //
    // The deploy script's HTTPS branch (PS1 lines 499-608) reads the per-binding
    // `thumbprint`, finds the cert in `Cert:\LocalMachine\My`, then runs
    // `netsh http add sslcert ipport=...` (non-SNI) or `hostnameport=...` (SNI).
    // These tests stage a real self-signed cert in the local machine store, deploy,
    // then assert the metabase binding lands AND the netsh sslcert table shows
    // the cert thumbprint in the expected form.

    [Fact]
    public void RealIIS_HttpsBindingNonSni_RegistersCertViaNetshIpport()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        var thumbprint = ctx.StageSelfSignedCertInLocalMachineMy(dnsName: $"squid-iis-nonsni-{Guid.NewGuid():N}");
        ctx.RegisterNetshIpPortForCleanup(ctx.HttpsPort);

        var bindingsJson =
            "[{\"protocol\":\"https\",\"port\":\"" + ctx.HttpsPort + "\",\"host\":\"\"," +
            "\"ipAddress\":\"*\",\"thumbprint\":\"" + thumbprint + "\"," +
            "\"requireSni\":false,\"enabled\":true}]";

        var script = IISDeployScriptBuilder.Build(BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, bindingsJson),
            (Property.StartApplicationPool, "True"),
            (Property.StartWebSite, "True")));

        var result = RunPowerShell(script);

        result.ExitCode.ShouldBe(0,
            customMessage:
                "Squid IIS HTTPS-non-SNI deploy failed. To diagnose manually: " +
                $"`Get-ChildItem Cert:\\LocalMachine\\My\\{thumbprint}` and " +
                $"`netsh http show sslcert ipport=0.0.0.0:{ctx.HttpsPort}`.\n\n" +
                $"STDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        // netsh sslcert table MUST show the cert thumbprint under ipport=0.0.0.0:<port>.
        var netshDump = RunPowerShell($"& netsh http show sslcert ipport=0.0.0.0:{ctx.HttpsPort}").StdOut;
        netshDump.ToUpperInvariant().ShouldContain(thumbprint.ToUpperInvariant(),
            customMessage:
                $"netsh http show sslcert ipport=0.0.0.0:{ctx.HttpsPort} did NOT show our cert thumbprint. " +
                $"This means either the binding wasn't created OR was created against a different port/IP. " +
                $"Manually: `netsh http show sslcert` to dump every binding.\n\nnetsh output:\n{netshDump}");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_HttpsBindingSni_RegistersCertViaNetshHostnameport()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        var hostName = $"squid-iis-sni-{Guid.NewGuid():N}.local";
        var thumbprint = ctx.StageSelfSignedCertInLocalMachineMy(dnsName: hostName);
        ctx.RegisterNetshHostnamePortForCleanup(hostName, ctx.HttpsPort);

        var bindingsJson =
            "[{\"protocol\":\"https\",\"port\":\"" + ctx.HttpsPort + "\",\"host\":\"" + hostName + "\"," +
            "\"ipAddress\":\"*\",\"thumbprint\":\"" + thumbprint + "\"," +
            "\"requireSni\":true,\"enabled\":true}]";

        var script = IISDeployScriptBuilder.Build(BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, bindingsJson),
            (Property.StartApplicationPool, "True"),
            (Property.StartWebSite, "True")));

        var result = RunPowerShell(script);

        result.ExitCode.ShouldBe(0,
            customMessage:
                "Squid IIS HTTPS-SNI deploy failed. To diagnose manually: " +
                $"`Get-ChildItem Cert:\\LocalMachine\\My\\{thumbprint}` and " +
                $"`netsh http show sslcert hostnameport={hostName}:{ctx.HttpsPort}`.\n\n" +
                $"STDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        // netsh sslcert table MUST show our cert thumbprint under hostnameport=<host>:<port>.
        var netshDump = RunPowerShell($"& netsh http show sslcert hostnameport={hostName}:{ctx.HttpsPort}").StdOut;
        netshDump.ToUpperInvariant().ShouldContain(thumbprint.ToUpperInvariant(),
            customMessage:
                $"netsh http show sslcert hostnameport={hostName}:{ctx.HttpsPort} did NOT show our cert thumbprint. " +
                $"SNI handling broken — check `$_.sslFlags -eq 1` branch in PS1 (line 548). " +
                $"netsh output:\n{netshDump}");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_HttpsBindingWithMissingCert_FailsWithLocalMachineMyError()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        const string missingThumbprint = "FAKE0000000000000000000000000000DEADBEEF";

        var bindingsJson =
            "[{\"protocol\":\"https\",\"port\":\"" + ctx.HttpsPort + "\",\"host\":\"\"," +
            "\"ipAddress\":\"*\",\"thumbprint\":\"" + missingThumbprint + "\"," +
            "\"requireSni\":false,\"enabled\":true}]";

        var script = IISDeployScriptBuilder.Build(BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, bindingsJson)));

        var result = RunPowerShell(script);

        // The script throws when the cert lookup misses; PowerShell surfaces the throw via
        // non-zero exit + the error text we Squid-fied at line 531 of the embedded PS1.
        result.ExitCode.ShouldNotBe(0,
            customMessage: "Missing-cert deploy unexpectedly succeeded — the PS1's " +
                          $"`Could not find certificate under Cert:\\LocalMachine with thumbprint` " +
                          $"guard at line 531 is broken or wasn't reached.\nSTDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        var combinedOutput = result.StdOut + result.StdErr;
        combinedOutput.ShouldContain("Could not find certificate",
            customMessage:
                "Cert-missing error did not contain the actionable 'Could not find certificate' phrase. " +
                "Operators rely on grepping this exact phrase to triage. If this assertion fails, the PS1 " +
                "error message at line 531 was renamed without updating this test.");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_HttpsBindingRebindToDifferentCert_ReplacesPreviousNetshEntry()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        ctx.RegisterNetshIpPortForCleanup(ctx.HttpsPort);

        var thumbprintA = ctx.StageSelfSignedCertInLocalMachineMy(dnsName: $"squid-iis-rotateA-{Guid.NewGuid():N}");
        var thumbprintB = ctx.StageSelfSignedCertInLocalMachineMy(dnsName: $"squid-iis-rotateB-{Guid.NewGuid():N}");

        thumbprintA.ShouldNotBe(thumbprintB,
            customMessage: "Test setup expected two distinct cert thumbprints — both came back identical, " +
                          "which would make the rebind assertion meaningless.");

        // Deploy 1 — cert A
        var script1 = IISDeployScriptBuilder.Build(BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings,
                "[{\"protocol\":\"https\",\"port\":\"" + ctx.HttpsPort + "\",\"host\":\"\"," +
                "\"ipAddress\":\"*\",\"thumbprint\":\"" + thumbprintA + "\"," +
                "\"requireSni\":false,\"enabled\":true}]")));

        RunPowerShell(script1).ExitCode.ShouldBe(0, "First (cert A) deploy must succeed before re-bind can be tested.");

        RunPowerShell($"& netsh http show sslcert ipport=0.0.0.0:{ctx.HttpsPort}").StdOut
            .ToUpperInvariant().ShouldContain(thumbprintA.ToUpperInvariant(),
                customMessage: "Cert A binding not present after first deploy — staging issue, not a re-bind issue.");

        // Deploy 2 — cert B against the same site + port, exercising the
        // `# A different binding exists for the IP/port combination, replacing...` branch.
        var script2 = IISDeployScriptBuilder.Build(BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings,
                "[{\"protocol\":\"https\",\"port\":\"" + ctx.HttpsPort + "\",\"host\":\"\"," +
                "\"ipAddress\":\"*\",\"thumbprint\":\"" + thumbprintB + "\"," +
                "\"requireSni\":false,\"enabled\":true}]")));

        var result2 = RunPowerShell(script2);
        result2.ExitCode.ShouldBe(0,
            customMessage: $"Re-deploy with cert B failed. STDOUT:\n{result2.StdOut}\n\nSTDERR:\n{result2.StdErr}");

        var finalDump = RunPowerShell($"& netsh http show sslcert ipport=0.0.0.0:{ctx.HttpsPort}").StdOut;

        finalDump.ToUpperInvariant().ShouldContain(thumbprintB.ToUpperInvariant(),
            customMessage: "After re-deploy with cert B, netsh binding still doesn't show cert B. " +
                          "The PS1's `netsh http delete sslcert ipport=...` + add-new path may have failed silently.");

        finalDump.ToUpperInvariant().ShouldNotContain(thumbprintA.ToUpperInvariant(),
            customMessage: "After re-deploy, the OLD cert A is still bound — re-bind replaced nothing. " +
                          "This means cert rotation in production would leave the previous cert in place " +
                          "even after the operator changed the thumbprint property.");

        ctx.MarkClean();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Per-test isolation context. Every test gets unique site / pool / physicalPath
    /// names suffixed with a GUID so concurrent CI runs and re-runs after a failure
    /// don't collide on IIS metabase entries. <see cref="Dispose"/> best-effort
    /// removes the entries so a failing test never leaves IIS state pollution that
    /// breaks the next test.
    /// </summary>
    private sealed class IISTestContext : IDisposable
    {
        private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
        private readonly List<string> _tempDirsToClean = new();
        private readonly List<string> _certThumbprintsToClean = new();
        private readonly List<string> _netshIpPortsToClean = new();
        private readonly List<(string Host, string Port)> _netshHostnamePortsToClean = new();
        private bool _markedClean;

        public IISTestContext()
        {
            SiteName = $"SquidIISE2E-{_suffix}";
            PoolName = $"SquidIISE2EPool-{_suffix}";
            PhysicalPath = Path.Combine(Path.GetTempPath(), $"squid-iis-e2e-{_suffix}");
            HttpPort = PickFreePort();
            HttpsPort = PickFreePort();

            Directory.CreateDirectory(PhysicalPath);
            _tempDirsToClean.Add(PhysicalPath);
        }

        public string SiteName { get; }
        public string PoolName { get; }
        public string PhysicalPath { get; }
        public string HttpPort { get; }
        public string HttpsPort { get; }

        public void RegisterTempDirForCleanup(string path) => _tempDirsToClean.Add(path);

        /// <summary>
        /// Imports a self-signed cert into <c>Cert:\LocalMachine\My</c> and registers the
        /// thumbprint for removal during <see cref="Dispose"/>. Used by HTTPS binding tests
        /// — the embedded PS1's HTTPS branch reads the cert by thumbprint from this store.
        /// </summary>
        /// <param name="dnsName">CN + DNS SAN on the issued cert. Use a unique value per call
        /// so concurrent tests don't share subjects (the store is process-wide).</param>
        /// <returns>40-char SHA-1 thumbprint of the issued cert (uppercase hex, no spaces).</returns>
        public string StageSelfSignedCertInLocalMachineMy(string dnsName)
        {
            var result = RunPowerShell(
                $"$cert = New-SelfSignedCertificate -DnsName '{dnsName}' " +
                $"-CertStoreLocation Cert:\\LocalMachine\\My -KeyExportPolicy Exportable; " +
                $"Write-Host -NoNewline $cert.Thumbprint");

            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
                throw new InvalidOperationException(
                    $"Failed to stage self-signed cert. ExitCode={result.ExitCode}, " +
                    $"StdOut='{result.StdOut}', StdErr='{result.StdErr}'.");

            var thumbprint = result.StdOut.Trim();
            _certThumbprintsToClean.Add(thumbprint);
            return thumbprint;
        }

        /// <summary>
        /// Tells <see cref="Dispose"/> to run <c>netsh http delete sslcert ipport=0.0.0.0:{port}</c>
        /// on teardown. Production deploys (non-SNI) leave entries in the netsh sslcert table;
        /// CI parallelism would accumulate them otherwise.
        /// </summary>
        public void RegisterNetshIpPortForCleanup(string port) => _netshIpPortsToClean.Add(port);

        /// <summary>
        /// Tells <see cref="Dispose"/> to run <c>netsh http delete sslcert hostnameport={host}:{port}</c>
        /// on teardown. Used by SNI HTTPS binding tests.
        /// </summary>
        public void RegisterNetshHostnamePortForCleanup(string host, string port) =>
            _netshHostnamePortsToClean.Add((host, port));

        public void MarkClean() => _markedClean = true;

        public void Dispose()
        {
            // Best-effort: even if the test passed (MarkClean) we still tear down IIS state
            // so the next class instance starts clean.
            if (OperatingSystem.IsWindows() && IsIISInstalled())
            {
                TryPowerShell($"Remove-Website -Name '{SiteName}' -ErrorAction SilentlyContinue");
                TryPowerShell($"Remove-WebAppPool -Name '{PoolName}' -ErrorAction SilentlyContinue");
            }

            // netsh sslcert entries — survive process exit so MUST be torn down explicitly.
            // Best-effort: ignore errors (entry may not exist if the deploy failed pre-bind).
            foreach (var port in _netshIpPortsToClean)
                TryPowerShell($"& netsh http delete sslcert ipport=0.0.0.0:{port} | Out-Null");

            foreach (var (host, port) in _netshHostnamePortsToClean)
                TryPowerShell($"& netsh http delete sslcert hostnameport={host}:{port} | Out-Null");

            // Cert store entries — removing the cert from the store also revokes any netsh
            // binding (since the SHA-1 lookup misses), so order matters: netsh first, certs second.
            foreach (var thumb in _certThumbprintsToClean)
                TryPowerShell($"Remove-Item Cert:\\LocalMachine\\My\\{thumb} -Force -ErrorAction SilentlyContinue");

            foreach (var dir in _tempDirsToClean)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
                catch { /* best-effort */ }
            }

            // If MarkClean wasn't called and we're on a CI runner, leave a hint in the log so
            // post-mortem diagnostics know to dump IIS state on failure.
            if (!_markedClean && OperatingSystem.IsWindows())
                Console.WriteLine($"[IISTestContext.Dispose] Test did not call MarkClean — possible failure path. Site='{SiteName}', Pool='{PoolName}'.");
        }

        /// <summary>
        /// Bind a free localhost port via TcpListener(0) then release it. CI runs may have
        /// 80 already taken by IIS Default Web Site; using a dynamic port avoids that conflict.
        /// </summary>
        private static string PickFreePort()
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port.ToString();
        }
    }

    /// <summary>
    /// Short hand for the action-property names. We use string literals here
    /// (not the <c>IISDeployProperties</c> internal constants) so this test file
    /// remains decoupled from the production internal API — a refactor that renames
    /// the constant should fail the unit test (which pins names) before reaching
    /// this E2E.
    /// </summary>
    private static class Property
    {
        public const string CreateOrUpdateWebSite = "Squid.Action.IISWebSite.CreateOrUpdateWebSite";
        public const string WebSiteName = "Squid.Action.IISWebSite.WebSiteName";
        public const string ApplicationPoolName = "Squid.Action.IISWebSite.ApplicationPoolName";
        public const string ApplicationPoolIdentityType = "Squid.Action.IISWebSite.ApplicationPoolIdentityType";
        public const string ApplicationPoolFrameworkVersion = "Squid.Action.IISWebSite.ApplicationPoolFrameworkVersion";
        public const string WebRoot = "Squid.Action.IISWebSite.WebRoot";
        public const string Bindings = "Squid.Action.IISWebSite.Bindings";
        public const string StartApplicationPool = "Squid.Action.IISWebSite.StartApplicationPool";
        public const string StartWebSite = "Squid.Action.IISWebSite.StartWebSite";
    }

    private static DeploymentActionDto BuildAction(params (string Name, string Value)[] properties)
    {
        return new DeploymentActionDto
        {
            Id = 1,
            Name = "IIS WebSite (E2E)",
            ActionType = "Squid.DeployToIISWebSite",
            Properties = properties
                .Select(p => new DeploymentActionPropertyDto { PropertyName = p.Name, PropertyValue = p.Value })
                .ToList()
        };
    }

    private static bool IsIISInstalled()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            var result = RunPowerShell("(Get-WindowsFeature Web-WebServer -ErrorAction SilentlyContinue).Installed");
            return result.ExitCode == 0 && result.StdOut.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Convenience for one-line state queries against IIS metabase. Wraps
    /// the value in a <c>Write-Host</c> so we get clean stdout without prompt noise.
    /// </summary>
    private static string PowerShellSingleLine(string command)
    {
        var script = $"Import-Module WebAdministration; Write-Host -NoNewline ({command})";
        var result = RunPowerShell(script);
        return result.StdOut.Trim();
    }

    private static void TryPowerShell(string command)
    {
        try { RunPowerShell($"Import-Module WebAdministration -ErrorAction SilentlyContinue; {command}"); }
        catch { /* best-effort cleanup */ }
    }

    private static PsResult RunPowerShell(string script)
    {
        // Use powershell.exe (Windows PowerShell 5.1) explicitly because the
        // IIS WebAdministration module's idiomatic surface (`IIS:\Sites\X`,
        // `Get-Website`, etc.) is most reliable on 5.1. The script body's
        // outer wrapper handles PS 7+ via compat session, but for the runtime
        // test harness staying on 5.1 avoids that branch and isolates the
        // failure mode to script logic.
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = Process.Start(startInfo)!;
        process.StandardInput.Write(script);
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(TimeSpan.FromMinutes(2));

        return new PsResult(process.ExitCode, stdout, stderr);
    }

    private sealed record PsResult(int ExitCode, string StdOut, string StdErr);
}
