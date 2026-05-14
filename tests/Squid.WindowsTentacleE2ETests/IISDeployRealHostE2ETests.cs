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

    [Theory]
    [InlineData("v4.0", "v4.0")]              // modern .NET Framework (default for new pools)
    [InlineData("v2.0", "v2.0")]              // legacy .NET Framework 2.0/3.5 apps
    [InlineData("No Managed Code", "")]       // static-content / classic-ASP / non-.NET payloads
    public void RealIIS_AppPoolFrameworkVersion_AppliedToMetabase(string configured, string expectedInMetabase)
    {
        // PS1 lines 227-234 branch on the literal string "No Managed Code" — that branch
        // sets managedRuntimeVersion to empty string. Every other value is passed through verbatim.
        // Theory covers both arms.
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();

        var script = IISDeployScriptBuilder.Build(BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolFrameworkVersion, configured),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, "[{\"protocol\":\"http\",\"port\":\"" + ctx.HttpPort + "\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}]")));

        var result = RunPowerShell(script);
        result.ExitCode.ShouldBe(0,
            customMessage: $"Deploy failed for framework='{configured}'. STDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        var poolRuntime = PowerShellSingleLine(
            $"(Get-ItemProperty IIS:\\AppPools\\{ctx.PoolName} -Name managedRuntimeVersion).Value");

        poolRuntime.ShouldBe(expectedInMetabase,
            customMessage:
                $"Framework version not applied. Configured='{configured}', expectedInMetabase='{expectedInMetabase}', actual='{poolRuntime}'. " +
                $"PS1 lines 227-234 should set `managedRuntimeVersion` to '{expectedInMetabase}' for input '{configured}'. " +
                $"Manually: `Get-ItemProperty IIS:\\AppPools\\{ctx.PoolName} -Name managedRuntimeVersion`.");

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

    // ── Authentication toggles (Phase 3) ───────────────────────────────────
    //
    // The deploy script (PS1 lines 778-806) runs appcmd.exe three times per deploy:
    // `appcmd set config <site> /section:.../anonymousAuthentication /enabled:<X>`
    // and the equivalent for basic + windows. These tests verify the operator's
    // configured flag values land in the IIS metabase, by reading back via the
    // same appcmd tool. The Theory matrix covers the realistic combinations an
    // operator picks (anon-only dev / windows-only enterprise / locked-down none /
    // wide-open all-three).
    //
    // Skips: needs Web-Basic-Auth + Web-Windows-Auth Windows features installed.
    // The workflow installs these; local dev hosts without them see a clean skip.

    [Theory]
    [InlineData("True", "False", "False")]    // anonymous-only (typical dev)
    [InlineData("False", "True", "False")]    // basic-only (rare but supported)
    [InlineData("False", "False", "True")]    // windows-only (typical enterprise intranet)
    [InlineData("True", "True", "False")]     // anon + basic
    [InlineData("False", "False", "False")]   // locked-down (all three explicitly off)
    [InlineData("True", "True", "True")]      // wide-open (every method enabled)
    public void RealIIS_AuthFlags_MetabaseReflectsConfiguredEnabledStates(
        string anonEnabled, string basicEnabled, string windowsEnabled)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;
        if (!IsIISFeatureInstalled("Web-Basic-Auth")) return;
        if (!IsIISFeatureInstalled("Web-Windows-Auth")) return;

        using var ctx = new IISTestContext();

        var script = IISDeployScriptBuilder.Build(BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings,
                "[{\"protocol\":\"http\",\"port\":\"" + ctx.HttpPort + "\",\"host\":\"\"," +
                "\"ipAddress\":\"*\",\"enabled\":true}]"),
            (Property.EnableAnonymousAuthentication, anonEnabled),
            (Property.EnableBasicAuthentication, basicEnabled),
            (Property.EnableWindowsAuthentication, windowsEnabled),
            (Property.StartApplicationPool, "True"),
            (Property.StartWebSite, "True")));

        var result = RunPowerShell(script);

        result.ExitCode.ShouldBe(0,
            customMessage:
                $"Auth-flag deploy failed for combo (anon={anonEnabled}, basic={basicEnabled}, windows={windowsEnabled}). " +
                $"Manually inspect via `appcmd.exe list config {ctx.SiteName} -section:system.webServer/security/authentication/anonymousAuthentication`.\n\n" +
                $"STDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        var actualAnon = ReadAuthEnabledFlag(ctx.SiteName, "anonymousAuthentication");
        var actualBasic = ReadAuthEnabledFlag(ctx.SiteName, "basicAuthentication");
        var actualWindows = ReadAuthEnabledFlag(ctx.SiteName, "windowsAuthentication");

        // appcmd outputs `enabled="true"` / `enabled="false"` (lowercase XML attr).
        // The operator's configured value can be "True"/"False" (PascalCase, common in Squid)
        // or "true"/"false". Compare case-insensitively.
        actualAnon.ToLowerInvariant().ShouldBe(anonEnabled.ToLowerInvariant(),
            customMessage:
                $"anonymousAuthentication.enabled in metabase doesn't match configured value. " +
                $"Configured='{anonEnabled}', actual='{actualAnon}'. " +
                $"This means the PS1's appcmd call for the anonymous section didn't take effect.");

        actualBasic.ToLowerInvariant().ShouldBe(basicEnabled.ToLowerInvariant(),
            customMessage:
                $"basicAuthentication.enabled in metabase doesn't match. Configured='{basicEnabled}', actual='{actualBasic}'.");

        actualWindows.ToLowerInvariant().ShouldBe(windowsEnabled.ToLowerInvariant(),
            customMessage:
                $"windowsAuthentication.enabled in metabase doesn't match. Configured='{windowsEnabled}', actual='{actualWindows}'.");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_AuthFlag_FlipsOnRedeploy_MetabaseReflectsNewValue()
    {
        // Idempotence + change-detection: a redeploy with different auth flags
        // must produce the new state, not be a no-op. This catches a regression
        // where a future PR adds "skip if value matches" logic that doesn't account
        // for an externally-modified config (operator manually toggled in IIS Manager).
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;
        if (!IsIISFeatureInstalled("Web-Windows-Auth")) return;

        using var ctx = new IISTestContext();

        // Deploy 1: Anonymous on, Windows off
        var script1 = IISDeployScriptBuilder.Build(BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings,
                "[{\"protocol\":\"http\",\"port\":\"" + ctx.HttpPort + "\",\"host\":\"\"," +
                "\"ipAddress\":\"*\",\"enabled\":true}]"),
            (Property.EnableAnonymousAuthentication, "True"),
            (Property.EnableWindowsAuthentication, "False")));

        RunPowerShell(script1).ExitCode.ShouldBe(0, "First deploy (anon=true, windows=false) must succeed.");

        ReadAuthEnabledFlag(ctx.SiteName, "anonymousAuthentication").ToLowerInvariant().ShouldBe("true");
        ReadAuthEnabledFlag(ctx.SiteName, "windowsAuthentication").ToLowerInvariant().ShouldBe("false");

        // Deploy 2: Anonymous off, Windows on — opposite of Deploy 1
        var script2 = IISDeployScriptBuilder.Build(BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings,
                "[{\"protocol\":\"http\",\"port\":\"" + ctx.HttpPort + "\",\"host\":\"\"," +
                "\"ipAddress\":\"*\",\"enabled\":true}]"),
            (Property.EnableAnonymousAuthentication, "False"),
            (Property.EnableWindowsAuthentication, "True")));

        var result2 = RunPowerShell(script2);
        result2.ExitCode.ShouldBe(0,
            customMessage: $"Redeploy (anon=false, windows=true) failed.\nSTDOUT:\n{result2.StdOut}\n\nSTDERR:\n{result2.StdErr}");

        // Both flags must have FLIPPED — not just stayed at Deploy 1's values.
        ReadAuthEnabledFlag(ctx.SiteName, "anonymousAuthentication").ToLowerInvariant().ShouldBe("false",
            customMessage: "Redeploy with anonymous=False didn't flip the metabase from True to False. " +
                          "The PS1 may have a `skip if already configured` regression.");

        ReadAuthEnabledFlag(ctx.SiteName, "windowsAuthentication").ToLowerInvariant().ShouldBe("true",
            customMessage: "Redeploy with windows=True didn't flip the metabase from False to True. " +
                          "Confirm by `appcmd list config " + ctx.SiteName + " -section:system.webServer/security/authentication/windowsAuthentication` after the redeploy.");

        ctx.MarkClean();
    }

    // ── Rigor-hardening: app pool identities (Phase 3.5) ────────────────────
    //
    // The PS1's `SetUp-ApplicationPool` function (lines 214-222) branches on
    // `$applicationPoolIdentityType -eq "SpecificUser"`. For SpecificUser it sets
    // identityType + username + password; for every other value it sets just
    // identityType. Phase 1 only exercised `ApplicationPoolIdentity` (the modern
    // per-pool virtual account). These tests cover the 4 built-in service accounts
    // + SpecificUser-with-credentials so every IIS-supported identity is exercised
    // at least once.

    [Theory]
    [InlineData("ApplicationPoolIdentity")]   // Phase 1 covered this — kept for full matrix
    [InlineData("LocalSystem")]               // legacy, full machine privilege
    [InlineData("NetworkService")]            // legacy domain-network identity
    [InlineData("LocalService")]              // legacy local-only low privilege
    public void RealIIS_AppPoolIdentityType_BuiltIn_AppliedToMetabase(string identityType)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();

        var script = IISDeployScriptBuilder.Build(BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, identityType),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings,
                "[{\"protocol\":\"http\",\"port\":\"" + ctx.HttpPort + "\",\"host\":\"\"," +
                "\"ipAddress\":\"*\",\"enabled\":true}]")));

        var result = RunPowerShell(script);
        result.ExitCode.ShouldBe(0,
            customMessage:
                $"Deploy failed for identityType='{identityType}'. STDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        var actualIdentity = PowerShellSingleLine(
            $"(Get-ItemProperty IIS:\\AppPools\\{ctx.PoolName} -Name processModel.identityType).Value");

        actualIdentity.ShouldBe(identityType,
            customMessage:
                $"App pool identity not applied. Configured='{identityType}', actual='{actualIdentity}'. " +
                $"Manually: `Get-ItemProperty IIS:\\AppPools\\{ctx.PoolName} -Name processModel`.");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_AppPoolIdentitySpecificUser_AppliesCredentialsToMetabase()
    {
        // SpecificUser path is the most security-sensitive identity option — operators
        // use it for domain-joined enterprise pools. PS1 line 217-218 sets the full
        // processModel triple (identityType + username + password). This test stages
        // a local Windows user via `net user /add`, deploys with that user as the pool
        // identity, then verifies BOTH the identityType and userName landed in the
        // metabase. We don't try to START the pool because that requires granting
        // SeBatchLogonRight to the local user (LSA-policy edit, out of scope for
        // a deploy-step E2E).
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        var (userName, password) = ctx.StageLocalWindowsUser();

        var script = IISDeployScriptBuilder.Build(BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "SpecificUser"),
            (Property.ApplicationPoolUsername, userName),
            (Property.ApplicationPoolPassword, password),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings,
                "[{\"protocol\":\"http\",\"port\":\"" + ctx.HttpPort + "\",\"host\":\"\"," +
                "\"ipAddress\":\"*\",\"enabled\":true}]"),
            // Pool may not start because SeBatchLogonRight isn't granted — that's fine,
            // we're testing the metabase write, not the runtime.
            (Property.StartApplicationPool, "False"),
            (Property.StartWebSite, "False")));

        var result = RunPowerShell(script);
        result.ExitCode.ShouldBe(0,
            customMessage:
                $"SpecificUser pool deploy failed. STDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}\n\n" +
                $"If failure mentions 'The user name or password is incorrect', the cleanup may have removed " +
                $"the account too eagerly. If it mentions 'Cannot validate the access' the local-user creation " +
                $"itself failed.");

        var actualIdentity = PowerShellSingleLine(
            $"(Get-ItemProperty IIS:\\AppPools\\{ctx.PoolName} -Name processModel.identityType).Value");

        actualIdentity.ShouldBe("SpecificUser",
            customMessage: $"Expected identityType=SpecificUser, actual='{actualIdentity}'.");

        // The userName property contains a non-empty value. We don't assert exact match
        // because IIS may store it with domain-prefix (`.\squid-test-...`) — what we
        // need to verify is that PS1 wrote it through, not lost it to escaping.
        var actualUserName = PowerShellSingleLine(
            $"(Get-ItemProperty IIS:\\AppPools\\{ctx.PoolName} -Name processModel.userName).Value");

        actualUserName.ShouldContain(userName,
            customMessage:
                $"Pool userName doesn't contain configured user '{userName}', actual='{actualUserName}'. " +
                $"This means the PS1 lost the username during the `Set-ItemProperty processModel` call — " +
                $"check apostrophe escaping or hashtable serialization.");

        ctx.MarkClean();
    }

    // ── Rigor-hardening: bindings edge cases (Phase 3.5) ────────────────────

    [Fact]
    public void RealIIS_ExistingBindingsMerge_PreservesPreExistingBindings()
    {
        // The PS1 `ExistingBindings` property has two modes:
        //   - default (Replace): the deploy resets the site's bindings to exactly what's
        //     in the configured array; pre-existing bindings the operator added in IIS
        //     Manager would be wiped.
        //   - "Merge": pre-existing bindings whose key (protocol+bindingInformation) doesn't
        //     conflict with the configured set are preserved.
        // PS1 line 663-668 implements the merge by reading the site's current bindings
        // and adding the non-conflicting ones into $wsbindings before applying.
        //
        // This test pre-creates a site with TWO bindings, then re-deploys with Merge
        // mode and only ONE binding configured. The pre-existing binding on the other
        // port must survive.
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        var preExistingPort = ctx.PickAdditionalFreePort();

        // Step 1: Pre-create the site + app pool with TWO bindings (operator-staged state).
        // We use New-Website directly so this is "manual" config that didn't come from Squid.
        var setupResult = RunPowerShell(
            $"Import-Module WebAdministration; " +
            $"New-WebAppPool -Name '{ctx.PoolName}' | Out-Null; " +
            $"New-Website -Name '{ctx.SiteName}' -Port {preExistingPort} -PhysicalPath '{ctx.PhysicalPath}' " +
            $"  -ApplicationPool '{ctx.PoolName}' -Force | Out-Null; " +
            $"New-WebBinding -Name '{ctx.SiteName}' -Protocol 'http' -Port {ctx.HttpPort} -IPAddress '*' -HostHeader ''");

        setupResult.ExitCode.ShouldBe(0,
            customMessage: $"Pre-deploy site setup failed. STDOUT:\n{setupResult.StdOut}\n\nSTDERR:\n{setupResult.StdErr}");

        // Step 2: Deploy with Merge mode + only the HttpPort binding in the configured set.
        // The preExistingPort binding is NOT in the configured set, so under Replace it
        // would be removed. Under Merge it MUST survive.
        var script = IISDeployScriptBuilder.Build(BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings,
                "[{\"protocol\":\"http\",\"port\":\"" + ctx.HttpPort + "\",\"host\":\"\"," +
                "\"ipAddress\":\"*\",\"enabled\":true}]"),
            (Property.ExistingBindings, "Merge"),
            (Property.StartApplicationPool, "True"),
            (Property.StartWebSite, "True")));

        var result = RunPowerShell(script);
        result.ExitCode.ShouldBe(0,
            customMessage: $"Merge-mode deploy failed. STDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        // Step 3: Both bindings must be present.
        var bindingInfo = RunPowerShell(
            $"Import-Module WebAdministration; " +
            $"(Get-WebBinding -Name '{ctx.SiteName}' | ForEach-Object {{ $_.bindingInformation }}) -join ';'").StdOut.Trim();

        bindingInfo.ShouldContain($":{preExistingPort}:",
            customMessage:
                $"Pre-existing binding on port {preExistingPort} was wiped by the deploy. " +
                $"ExistingBindings=Merge should have preserved it. Actual binding info: '{bindingInfo}'. " +
                $"PS1 line 663-668 (`if ($existingBindings -eq \"Merge\")`) likely didn't fire.");

        bindingInfo.ShouldContain($":{ctx.HttpPort}:",
            customMessage:
                $"Configured binding on port {ctx.HttpPort} is missing after deploy. " +
                $"Actual binding info: '{bindingInfo}'.");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_EmptyBindingsArray_DeploysSiteWithoutOperatorBindings()
    {
        // Edge case: operator explicitly sets Bindings to `[]` (empty array). The PS1's
        // ConvertFrom-Json yields an empty array, the foreach over $bindingArray runs
        // zero times, and the site is left with whatever default-binding behaviour IIS
        // applies on site creation. This test pins that the deploy COMPLETES (doesn't
        // throw) when no bindings are configured — operators do hit this when they
        // pre-stage bindings externally and use Merge to layer Squid's deploys on top.
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
            (Property.Bindings, "[]"),
            (Property.StartApplicationPool, "True"),
            (Property.StartWebSite, "False")));      // not started — there may be no bindings to listen on

        var result = RunPowerShell(script);
        result.ExitCode.ShouldBe(0,
            customMessage:
                $"Empty bindings array deploy failed. The script should treat `[]` as 'no bindings to configure' " +
                $"and let the site be created. STDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        // Site must exist in the metabase regardless.
        var siteExists = PowerShellSingleLine(
            $"if (Get-Website -Name '{ctx.SiteName}' -ErrorAction SilentlyContinue) {{ 'true' }} else {{ 'false' }}");

        siteExists.ShouldBe("true",
            customMessage: $"Site '{ctx.SiteName}' not created despite the deploy reporting success. " +
                          $"`Get-Website -Name '{ctx.SiteName}'` to verify manually.");

        ctx.MarkClean();
    }

    // ── .NET Configuration Variables (Phase 6) ──────────────────────────────
    //
    // The deploy script's `Update-IISConfigurationVariables` function walks *.config files
    // under WebRoot and replaces matching `appSettings/add@key`, `connectionStrings/add@name`,
    // `applicationSettings/.../setting@name` entries with values from the deployment's
    // variable set (shipped as `$SquidVariables`). These tests stage a real `web.config`
    // in the WebRoot, deploy with the feature enabled + a variable that matches a config
    // entry by name, then read the rewritten `web.config` and assert the replacement.

    [Fact]
    public void RealIIS_ConfigurationVariables_AppSettingReplacedByMatchingVariable()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        var webConfigPath = Path.Combine(ctx.PhysicalPath, "web.config");
        File.WriteAllText(webConfigPath,
            "<?xml version=\"1.0\"?>\n" +
            "<configuration>\n" +
            "  <appSettings>\n" +
            "    <add key=\"OrderApi.LogLevel\" value=\"OLD_LEVEL\" />\n" +
            "    <add key=\"UnrelatedSetting\" value=\"DO_NOT_TOUCH\" />\n" +
            "  </appSettings>\n" +
            "</configuration>\n");

        var variables = new List<Squid.Message.Models.Deployments.Variable.VariableDto>
        {
            new() { Name = "OrderApi.LogLevel", Value = "Debug" }
        };

        var action = BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]"),
            (Property.ConfigurationVariablesEnabled, "True"));

        var script = IISDeployScriptBuilder.Build(action, variables);
        var result = RunPowerShell(script);

        result.ExitCode.ShouldBe(0,
            customMessage: $"Config-vars deploy failed.\nSTDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        var rewritten = File.ReadAllText(webConfigPath);

        rewritten.ShouldContain("key=\"OrderApi.LogLevel\"",
            customMessage: "Original appSetting key disappeared — rewriter deleted the node instead of updating it.");
        rewritten.ShouldContain("value=\"Debug\"",
            customMessage: $"appSettings[OrderApi.LogLevel] value not replaced. Expected 'Debug', actual file:\n{rewritten}");
        rewritten.ShouldNotContain("OLD_LEVEL",
            customMessage: "Original value still present — replacement didn't fire or didn't persist.");
        rewritten.ShouldContain("value=\"DO_NOT_TOUCH\"",
            customMessage: "Unrelated appSetting was touched — rewriter is replacing more than the matching key.");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_ConfigurationVariables_ConnectionStringReplacedByMatchingVariable()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        var webConfigPath = Path.Combine(ctx.PhysicalPath, "web.config");
        File.WriteAllText(webConfigPath,
            "<?xml version=\"1.0\"?>\n" +
            "<configuration>\n" +
            "  <connectionStrings>\n" +
            "    <add name=\"OrdersDb\" connectionString=\"Server=OLD_HOST;Database=Orders\" providerName=\"System.Data.SqlClient\" />\n" +
            "    <add name=\"UnusedDb\" connectionString=\"Server=other-host;Database=Other\" />\n" +
            "  </connectionStrings>\n" +
            "</configuration>\n");

        const string newConnection = "Server=new-host;Database=Orders;Trusted_Connection=True";

        var variables = new List<Squid.Message.Models.Deployments.Variable.VariableDto>
        {
            new() { Name = "OrdersDb", Value = newConnection }
        };

        var action = BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]"),
            (Property.ConfigurationVariablesEnabled, "True"));

        var script = IISDeployScriptBuilder.Build(action, variables);
        var result = RunPowerShell(script);

        result.ExitCode.ShouldBe(0,
            customMessage: $"Config-vars (connectionStrings) deploy failed.\nSTDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        var rewritten = File.ReadAllText(webConfigPath);

        rewritten.ShouldContain($"connectionString=\"{newConnection}\"",
            customMessage: $"connectionString for 'OrdersDb' not replaced. Expected '{newConnection}', actual file:\n{rewritten}");
        rewritten.ShouldNotContain("Server=OLD_HOST",
            customMessage: "Old connectionString value still present — replacement didn't persist.");
        rewritten.ShouldContain("name=\"UnusedDb\"",
            customMessage: "UnusedDb entry disappeared — rewriter deleted unrelated node.");
        rewritten.ShouldContain("connectionString=\"Server=other-host;Database=Other\"",
            customMessage: "UnusedDb connectionString was changed — rewriter touched unrelated node.");

        // Sanity: providerName attribute on OrdersDb must remain (rewriter only touches connectionString).
        rewritten.ShouldContain("providerName=\"System.Data.SqlClient\"",
            customMessage: "providerName attribute lost on OrdersDb — rewriter overwrites siblings.");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_ConfigurationVariables_FeatureDisabled_NoFileChanges()
    {
        // The enablement gate (`Squid.Action.IISWebSite.ConfigurationVariables.Enabled`) MUST
        // guard the rewriter. When unset or != "True", the rewriter does not run even if
        // matching variables are present.
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        var webConfigPath = Path.Combine(ctx.PhysicalPath, "web.config");
        const string originalContent =
            "<?xml version=\"1.0\"?>\n" +
            "<configuration>\n" +
            "  <appSettings>\n" +
            "    <add key=\"OrderApi.LogLevel\" value=\"INITIAL\" />\n" +
            "  </appSettings>\n" +
            "</configuration>\n";
        File.WriteAllText(webConfigPath, originalContent);

        var variables = new List<Squid.Message.Models.Deployments.Variable.VariableDto>
        {
            new() { Name = "OrderApi.LogLevel", Value = "SHOULD_NOT_BE_APPLIED" }
        };

        var action = BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]")
            // ConfigurationVariablesEnabled NOT set
        );

        var script = IISDeployScriptBuilder.Build(action, variables);
        var result = RunPowerShell(script);

        result.ExitCode.ShouldBe(0,
            customMessage: $"Deploy with feature off failed.\nSTDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        var content = File.ReadAllText(webConfigPath);
        content.ShouldContain("value=\"INITIAL\"",
            customMessage: "Original web.config value lost despite feature being OFF — gate is broken.");
        content.ShouldNotContain("SHOULD_NOT_BE_APPLIED",
            customMessage: "Rewriter ran despite feature being off — gate is broken.");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_ConfigurationVariables_NoMatchingVariable_EntryUntouched()
    {
        // The rewriter only replaces values when a Squid variable with the SAME NAME as the
        // config entry's key/name exists. Operators may have config entries that don't have
        // corresponding variables — those must pass through untouched.
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        var webConfigPath = Path.Combine(ctx.PhysicalPath, "web.config");
        File.WriteAllText(webConfigPath,
            "<?xml version=\"1.0\"?>\n" +
            "<configuration>\n" +
            "  <appSettings>\n" +
            "    <add key=\"OrphanSetting\" value=\"NO_VARIABLE_FOR_ME\" />\n" +
            "  </appSettings>\n" +
            "</configuration>\n");

        var variables = new List<Squid.Message.Models.Deployments.Variable.VariableDto>
        {
            // Different name from the appSetting key — must NOT match.
            new() { Name = "SomeOtherKey", Value = "wont-apply" }
        };

        var action = BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]"),
            (Property.ConfigurationVariablesEnabled, "True"));

        var script = IISDeployScriptBuilder.Build(action, variables);
        var result = RunPowerShell(script);

        result.ExitCode.ShouldBe(0, customMessage: $"Deploy failed:\n{result.StdErr}");

        var content = File.ReadAllText(webConfigPath);
        content.ShouldContain("value=\"NO_VARIABLE_FOR_ME\"",
            customMessage: "OrphanSetting was modified despite no matching variable — rewriter is over-eager.");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_ConfigurationVariables_NamespacedWebConfig_StillReplacesEntries()
    {
        // Octopus parity edge case: real-world web.config files often declare an XML namespace
        // (`<configuration xmlns="..."`). A naive `//appSettings/add` XPath returns ZERO matches
        // in that case — operator would see no replacements and no error. Octopus uses
        // `//*[local-name()='appSettings']/*[local-name()='add']` to match regardless of namespace
        // (`ConfigurationVariablesReplacer.cs:77`). This test pins our parity by feeding a
        // namespaced config and verifying the replacement still happens.
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        var webConfigPath = Path.Combine(ctx.PhysicalPath, "web.config");
        File.WriteAllText(webConfigPath,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<configuration xmlns=\"http://example.com/schemas/config\">\n" +
            "  <appSettings>\n" +
            "    <add key=\"NamespacedKey\" value=\"OLD_NS_VALUE\" />\n" +
            "  </appSettings>\n" +
            "</configuration>\n");

        var variables = new List<Squid.Message.Models.Deployments.Variable.VariableDto>
        {
            new() { Name = "NamespacedKey", Value = "NEW_NS_VALUE" }
        };

        var action = BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]"),
            (Property.ConfigurationVariablesEnabled, "True"));

        var script = IISDeployScriptBuilder.Build(action, variables);
        var result = RunPowerShell(script);

        result.ExitCode.ShouldBe(0,
            customMessage: $"Namespaced web.config deploy failed.\nSTDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        var rewritten = File.ReadAllText(webConfigPath);

        rewritten.ShouldContain("value=\"NEW_NS_VALUE\"",
            customMessage:
                $"Namespaced web.config NOT updated — XPath probably regressed to namespace-aware form. " +
                $"Octopus parity (ConfigurationVariablesReplacer.cs:77) requires `local-name()` XPath. " +
                $"File after deploy:\n{rewritten}");

        rewritten.ShouldNotContain("OLD_NS_VALUE",
            customMessage: "Original value still present despite namespace fix — replacement didn't fire.");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_ConfigurationVariables_ApplicationSettingsWithoutValueElement_CreatesValueElement()
    {
        // Octopus parity edge case: <setting name="X"/> with no child <value> element.
        // Octopus's ReplaceStronglyTypeApplicationSetting (ConfigurationVariablesReplacer.cs:148-151)
        // CREATES the <value> child and sets its inner text. Earlier Squid implementation silently
        // skipped (`if ($null -ne $valueNode)` gate). The fix preserves operator-facing parity.
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        var webConfigPath = Path.Combine(ctx.PhysicalPath, "web.config");
        File.WriteAllText(webConfigPath,
            "<?xml version=\"1.0\"?>\n" +
            "<configuration>\n" +
            "  <applicationSettings>\n" +
            "    <MyApp.Properties.Settings>\n" +
            "      <setting name=\"BareSetting\" serializeAs=\"String\" />\n" +
            "    </MyApp.Properties.Settings>\n" +
            "  </applicationSettings>\n" +
            "</configuration>\n");

        var variables = new List<Squid.Message.Models.Deployments.Variable.VariableDto>
        {
            new() { Name = "BareSetting", Value = "CREATED_VALUE" }
        };

        var action = BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]"),
            (Property.ConfigurationVariablesEnabled, "True"));

        var script = IISDeployScriptBuilder.Build(action, variables);
        var result = RunPowerShell(script);

        result.ExitCode.ShouldBe(0,
            customMessage: $"applicationSettings-without-value deploy failed.\nSTDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        var rewritten = File.ReadAllText(webConfigPath);

        // The <value>CREATED_VALUE</value> element must exist after the deploy.
        rewritten.ShouldContain("<value>CREATED_VALUE</value>",
            customMessage:
                $"applicationSettings <value> element NOT created. Octopus parity requires the rewriter " +
                $"to APPEND a <value> child when none exists. File after deploy:\n{rewritten}");

        ctx.MarkClean();
    }

    // ── Package extraction (Phase 10) ────────────────────────────────────────
    //
    // The deploy script's `Expand-IISPackage` function extracts an operator-staged `.zip` or
    // `.nupkg` into `Package.ExtractTo` (or WebRoot by default). Hook runs FIRST among the
    // pre-IIS pipeline so all subsequent rewriters operate on the extracted files.
    //
    // These tests stage a real archive at a temp path, deploy with the feature on, and assert
    // the extracted files land where expected — proving the operator's "package staged in prior
    // step + IIS step extracts to WebRoot" workflow.

    [Fact]
    public void RealIIS_PackageExtraction_ZipExtractedIntoWebRoot()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();

        // Stage a sample .zip with a known file inside.
        var zipPath = Path.Combine(Path.GetTempPath(), $"squid-iis-package-{Guid.NewGuid():N}.zip");
        ctx.RegisterSentinelPath("package-zip");  // borrows the cleanup mechanism
        var stagingDir = Path.Combine(Path.GetTempPath(), $"squid-iis-pkg-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);
        ctx.RegisterTempDirForCleanup(stagingDir);
        File.WriteAllText(Path.Combine(stagingDir, "index.html"), "<html>hello from package</html>");
        File.WriteAllText(Path.Combine(stagingDir, "config.txt"), "deployed=true");
        System.IO.Compression.ZipFile.CreateFromDirectory(stagingDir, zipPath);
        ctx.RegisterTempDirForCleanup(zipPath);  // single-file cleanup; Dispose handles non-dirs gracefully

        var action = BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]"),
            (Property.PackageSourcePath, zipPath));

        var script = IISDeployScriptBuilder.Build(action);
        var result = RunPowerShell(script);

        result.ExitCode.ShouldBe(0,
            customMessage: $"Package extraction deploy failed.\nSTDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        // Both files must exist in WebRoot.
        File.Exists(Path.Combine(ctx.PhysicalPath, "index.html")).ShouldBeTrue(
            customMessage: $"index.html not extracted into WebRoot '{ctx.PhysicalPath}'. Directory contents: {string.Join(", ", Directory.GetFiles(ctx.PhysicalPath))}");
        File.Exists(Path.Combine(ctx.PhysicalPath, "config.txt")).ShouldBeTrue();
        File.ReadAllText(Path.Combine(ctx.PhysicalPath, "config.txt")).ShouldContain("deployed=true");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_PackageExtraction_PurgeBeforeExtractDeletesPriorFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();

        // Pre-stage a stale file in WebRoot — purge must remove it.
        File.WriteAllText(Path.Combine(ctx.PhysicalPath, "stale-old-deploy.txt"), "from a previous deploy");

        var zipPath = Path.Combine(Path.GetTempPath(), $"squid-iis-package-{Guid.NewGuid():N}.zip");
        var stagingDir = Path.Combine(Path.GetTempPath(), $"squid-iis-pkg-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);
        ctx.RegisterTempDirForCleanup(stagingDir);
        File.WriteAllText(Path.Combine(stagingDir, "new-only.txt"), "from the new deploy");
        System.IO.Compression.ZipFile.CreateFromDirectory(stagingDir, zipPath);
        ctx.RegisterTempDirForCleanup(zipPath);

        var action = BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]"),
            (Property.PackageSourcePath, zipPath),
            (Property.PackagePurgeBeforeExtract, "True"));

        var script = IISDeployScriptBuilder.Build(action);
        var result = RunPowerShell(script);

        result.ExitCode.ShouldBe(0, customMessage: $"Purge+extract deploy failed: {result.StdErr}");

        // Stale file MUST be gone (purge worked).
        File.Exists(Path.Combine(ctx.PhysicalPath, "stale-old-deploy.txt")).ShouldBeFalse(
            customMessage: "Stale file survived purge — PurgeBeforeExtract is broken or didn't run.");

        // New file MUST exist (extraction worked).
        File.Exists(Path.Combine(ctx.PhysicalPath, "new-only.txt")).ShouldBeTrue();

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_PackageExtraction_NoPurge_PriorFilesPreserved()
    {
        // Default (PurgeBeforeExtract not set or "False"): prior files survive the extract.
        // Extracted files merge with pre-existing ones; useful for layered deploys.
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        File.WriteAllText(Path.Combine(ctx.PhysicalPath, "prior-file.txt"), "kept from before");

        var zipPath = Path.Combine(Path.GetTempPath(), $"squid-iis-package-{Guid.NewGuid():N}.zip");
        var stagingDir = Path.Combine(Path.GetTempPath(), $"squid-iis-pkg-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);
        ctx.RegisterTempDirForCleanup(stagingDir);
        File.WriteAllText(Path.Combine(stagingDir, "from-package.txt"), "added by package");
        System.IO.Compression.ZipFile.CreateFromDirectory(stagingDir, zipPath);
        ctx.RegisterTempDirForCleanup(zipPath);

        var action = BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]"),
            (Property.PackageSourcePath, zipPath));

        var script = IISDeployScriptBuilder.Build(action);
        var result = RunPowerShell(script);
        result.ExitCode.ShouldBe(0, customMessage: $"No-purge extract deploy failed: {result.StdErr}");

        File.Exists(Path.Combine(ctx.PhysicalPath, "prior-file.txt")).ShouldBeTrue(
            customMessage: "Prior file deleted despite PurgeBeforeExtract NOT being set — purge is too eager.");
        File.Exists(Path.Combine(ctx.PhysicalPath, "from-package.txt")).ShouldBeTrue();

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_PackageExtraction_MissingSourcePath_FailsWithActionableError()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        var missingZipPath = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.zip");

        var action = BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]"),
            (Property.PackageSourcePath, missingZipPath));

        var script = IISDeployScriptBuilder.Build(action);
        var result = RunPowerShell(script);

        result.ExitCode.ShouldNotBe(0,
            customMessage: $"Missing-package deploy unexpectedly succeeded.\nSTDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        var combinedOutput = result.StdOut + result.StdErr;
        combinedOutput.ShouldContain("does not exist",
            customMessage: $"Error message didn't contain actionable 'does not exist' phrase. Output:\n{combinedOutput}");

        // IIS site MUST NOT have been created — the package-extract throw should have aborted before IIS configure.
        PowerShellSingleLine($"if (Get-Website -Name '{ctx.SiteName}' -ErrorAction SilentlyContinue) {{ 'true' }} else {{ 'false' }}")
            .ShouldBe("false", customMessage: "Site created despite package-extract failure — abort path is broken.");

        ctx.MarkClean();
    }

    // ── Structured Configuration Variables (Phase 9) ─────────────────────────
    //
    // The deploy script's `Update-IISStructuredJsonConfiguration` walks operator-specified
    // JSON files, recurses into the object structure, and replaces leaf values whose path
    // matches a Squid variable (with `:` or `.` separator). Phase 9 MVP supports JSON;
    // YAML/properties are future work (Octopus's `StructuredConfigurationVariablesBehaviour`
    // covers more formats via separate parsers).
    //
    // These tests stage real `appsettings.json` files, deploy, then assert the rewritten
    // JSON's leaf values match the operator's variable values.

    [Fact]
    public void RealIIS_StructuredConfigurationVariables_TopLevelLeaf_ReplacedByMatchingVariable()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        var jsonPath = Path.Combine(ctx.PhysicalPath, "appsettings.json");
        File.WriteAllText(jsonPath,
            "{\n" +
            "  \"AppName\": \"OLD_NAME\",\n" +
            "  \"Unchanged\": \"keep-me\"\n" +
            "}\n");

        var variables = new List<Squid.Message.Models.Deployments.Variable.VariableDto>
        {
            new() { Name = "AppName", Value = "OrderApi" }
        };

        var action = BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]"),
            (Property.StructuredConfigurationVariablesEnabled, "True"),
            (Property.StructuredConfigurationVariablesTargets, "appsettings.json"));

        var script = IISDeployScriptBuilder.Build(action, variables);
        var result = RunPowerShell(script);
        result.ExitCode.ShouldBe(0, customMessage: $"Top-level leaf replacement deploy failed: {result.StdErr}");

        var rewrittenJson = File.ReadAllText(jsonPath);
        rewrittenJson.ShouldContain("\"OrderApi\"",
            customMessage: $"Top-level leaf value 'AppName' wasn't replaced. File:\n{rewrittenJson}");
        rewrittenJson.ShouldNotContain("OLD_NAME");
        rewrittenJson.ShouldContain("\"keep-me\"",
            customMessage: "Non-matching leaf was modified — walker is over-eager.");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_StructuredConfigurationVariables_NestedLeaf_ColonSeparator_ReplacedByMatchingVariable()
    {
        // .NET Core `appsettings.json` operators typically use colon-separated paths
        // (matches IConfiguration's section separator).
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        var jsonPath = Path.Combine(ctx.PhysicalPath, "appsettings.json");
        File.WriteAllText(jsonPath,
            "{\n" +
            "  \"Logging\": {\n" +
            "    \"LogLevel\": {\n" +
            "      \"Default\": \"Information\"\n" +
            "    }\n" +
            "  },\n" +
            "  \"ConnectionStrings\": {\n" +
            "    \"Default\": \"Server=localhost\"\n" +
            "  }\n" +
            "}\n");

        var variables = new List<Squid.Message.Models.Deployments.Variable.VariableDto>
        {
            new() { Name = "Logging:LogLevel:Default", Value = "Debug" },
            new() { Name = "ConnectionStrings:Default", Value = "Server=prod-cluster" }
        };

        var action = BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]"),
            (Property.StructuredConfigurationVariablesEnabled, "True"),
            (Property.StructuredConfigurationVariablesTargets, "appsettings.json"));

        var script = IISDeployScriptBuilder.Build(action, variables);
        var result = RunPowerShell(script);
        result.ExitCode.ShouldBe(0, customMessage: $"Nested-leaf colon-separator deploy failed: {result.StdErr}");

        var rewritten = File.ReadAllText(jsonPath);
        // PowerShell ConvertTo-Json may vary indentation between versions — assert on the
        // VALUE only to keep the test independent of formatting.
        rewritten.ShouldContain("\"Debug\"",
            customMessage: $"Logging:LogLevel:Default not replaced to 'Debug'. File:\n{rewritten}");
        rewritten.ShouldNotContain("Information",
            customMessage: "Original LogLevel value still present — replacement didn't persist.");
        rewritten.ShouldContain("Server=prod-cluster",
            customMessage: $"ConnectionStrings:Default not replaced. File:\n{rewritten}");
        rewritten.ShouldNotContain("Server=localhost");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_StructuredConfigurationVariables_NestedLeaf_DotSeparator_AlsoMatches()
    {
        // .NET Framework operators may use dot-separated variable names (more familiar from
        // `app.config` style). The rewriter must accept BOTH separator forms (Octopus parity).
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        var jsonPath = Path.Combine(ctx.PhysicalPath, "appsettings.json");
        File.WriteAllText(jsonPath,
            "{\n" +
            "  \"Logging\": {\n" +
            "    \"LogLevel\": {\n" +
            "      \"Default\": \"Information\"\n" +
            "    }\n" +
            "  }\n" +
            "}\n");

        // Dot separator instead of colon
        var variables = new List<Squid.Message.Models.Deployments.Variable.VariableDto>
        {
            new() { Name = "Logging.LogLevel.Default", Value = "Trace" }
        };

        var action = BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]"),
            (Property.StructuredConfigurationVariablesEnabled, "True"),
            (Property.StructuredConfigurationVariablesTargets, "appsettings.json"));

        var script = IISDeployScriptBuilder.Build(action, variables);
        var result = RunPowerShell(script);
        result.ExitCode.ShouldBe(0, customMessage: $"Dot-separator path deploy failed: {result.StdErr}");

        var rewritten = File.ReadAllText(jsonPath);
        rewritten.ShouldContain("Trace",
            customMessage:
                $"Logging.LogLevel.Default (dot separator) not replaced — dot path matching is broken. " +
                $"File:\n{rewritten}");
        rewritten.ShouldNotContain("Information");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_StructuredConfigurationVariables_FeatureDisabled_JsonFileUntouched()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        var jsonPath = Path.Combine(ctx.PhysicalPath, "appsettings.json");
        const string originalContent = "{\n  \"X\": \"keep-me\"\n}\n";
        File.WriteAllText(jsonPath, originalContent);

        var variables = new List<Squid.Message.Models.Deployments.Variable.VariableDto>
        {
            new() { Name = "X", Value = "SHOULD_NOT_BE_APPLIED" }
        };

        var action = BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]"),
            (Property.StructuredConfigurationVariablesTargets, "appsettings.json")
            // StructuredConfigurationVariablesEnabled NOT set — gate stays off
        );

        var script = IISDeployScriptBuilder.Build(action, variables);
        var result = RunPowerShell(script);
        result.ExitCode.ShouldBe(0, customMessage: $"Deploy failed: {result.StdErr}");

        File.ReadAllText(jsonPath).ShouldContain("keep-me",
            customMessage: "JSON file was modified despite feature OFF — gate is broken.");
        File.ReadAllText(jsonPath).ShouldNotContain("SHOULD_NOT_BE_APPLIED",
            customMessage: "Rewriter ran despite feature OFF — gate is broken.");

        ctx.MarkClean();
    }

    // ── SubstituteInFiles (Phase 8) ──────────────────────────────────────────
    //
    // The deploy script's `Update-IISFilesWithVariableSubstitution` reads each glob in
    // `TargetFiles`, replaces every `#{X}` token using the Squid variable set, writes back.
    // Works on ANY text format — JSON, YAML, properties, .txt. These tests stage real
    // operator-style files, deploy, then assert the rendered content matches what was expected.

    [Fact]
    public void RealIIS_SubstituteInFiles_JsonAppSettings_TokensReplacedWithVariableValues()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        var jsonPath = Path.Combine(ctx.PhysicalPath, "appsettings.json");

        File.WriteAllText(jsonPath,
            "{\n" +
            "  \"ApiUrl\": \"#{ApiUrl}\",\n" +
            "  \"LogLevel\": \"#{LogLevel}\",\n" +
            "  \"Constant\": \"unchanged\"\n" +
            "}\n");

        var variables = new List<Squid.Message.Models.Deployments.Variable.VariableDto>
        {
            new() { Name = "ApiUrl", Value = "https://api.prod.example.com" },
            new() { Name = "LogLevel", Value = "Information" }
        };

        var action = BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]"),
            (Property.SubstituteInFilesEnabled, "True"),
            (Property.SubstituteInFilesTargetFiles, "appsettings.json"));

        var script = IISDeployScriptBuilder.Build(action, variables);
        var result = RunPowerShell(script);

        result.ExitCode.ShouldBe(0,
            customMessage: $"SubstituteInFiles deploy failed.\nSTDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        var rendered = File.ReadAllText(jsonPath);
        rendered.ShouldContain("\"ApiUrl\": \"https://api.prod.example.com\"",
            customMessage: $"#{{ApiUrl}} token not replaced. File after deploy:\n{rendered}");
        rendered.ShouldContain("\"LogLevel\": \"Information\"");
        rendered.ShouldContain("\"Constant\": \"unchanged\"",
            customMessage: "Non-token content was modified — substitution should only touch `#{X}` patterns.");

        // No surviving tokens (all matched).
        rendered.ShouldNotContain("#{ApiUrl}");
        rendered.ShouldNotContain("#{LogLevel}");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_SubstituteInFiles_MultipleGlobs_AllMatchingFilesProcessed()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        var jsonPath = Path.Combine(ctx.PhysicalPath, "appsettings.json");
        var ymlPath = Path.Combine(ctx.PhysicalPath, "config.yml");

        File.WriteAllText(jsonPath, "{ \"value\": \"#{TheValue}\" }");
        File.WriteAllText(ymlPath, "value: #{TheValue}");

        var variables = new List<Squid.Message.Models.Deployments.Variable.VariableDto>
        {
            new() { Name = "TheValue", Value = "shared-value" }
        };

        var action = BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]"),
            (Property.SubstituteInFilesEnabled, "True"),
            // Multiple globs (newline separated) — both should be processed
            (Property.SubstituteInFilesTargetFiles, "appsettings.json\nconfig.yml"));

        var script = IISDeployScriptBuilder.Build(action, variables);
        var result = RunPowerShell(script);

        result.ExitCode.ShouldBe(0, customMessage: $"Multi-glob substitution failed: {result.StdErr}");

        File.ReadAllText(jsonPath).ShouldContain("\"value\": \"shared-value\"",
            customMessage: "appsettings.json wasn't substituted.");
        File.ReadAllText(ymlPath).ShouldContain("value: shared-value",
            customMessage: "config.yml wasn't substituted — newline-separated glob list may not have parsed correctly.");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_SubstituteInFiles_UnmatchedToken_LeftAsIs()
    {
        // Octopus parity: unresolved tokens (no matching Squid variable) are left INTACT in the
        // output file. Operators see the unresolved token in the deployed file and can fix the
        // variable spec on the next deploy.
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        var configPath = Path.Combine(ctx.PhysicalPath, "config.txt");
        File.WriteAllText(configPath, "Known: #{KnownVar}\nUnknown: #{NotDefined}");

        var variables = new List<Squid.Message.Models.Deployments.Variable.VariableDto>
        {
            new() { Name = "KnownVar", Value = "resolved" }
        };

        var action = BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]"),
            (Property.SubstituteInFilesEnabled, "True"),
            (Property.SubstituteInFilesTargetFiles, "config.txt"));

        var script = IISDeployScriptBuilder.Build(action, variables);
        var result = RunPowerShell(script);

        result.ExitCode.ShouldBe(0, customMessage: $"Deploy failed: {result.StdErr}");

        var rendered = File.ReadAllText(configPath);
        rendered.ShouldContain("Known: resolved",
            customMessage: "Known token wasn't replaced.");
        rendered.ShouldContain("Unknown: #{NotDefined}",
            customMessage:
                "Unknown token was replaced or removed — Octopus parity requires unresolved tokens to be LEFT AS-IS. " +
                $"File after deploy:\n{rendered}");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_SubstituteInFiles_FeatureDisabled_FileUntouched()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        var configPath = Path.Combine(ctx.PhysicalPath, "config.txt");
        const string originalContent = "Value: #{ShouldStayAsToken}";
        File.WriteAllText(configPath, originalContent);

        var variables = new List<Squid.Message.Models.Deployments.Variable.VariableDto>
        {
            new() { Name = "ShouldStayAsToken", Value = "DO_NOT_APPLY" }
        };

        var action = BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]"),
            (Property.SubstituteInFilesTargetFiles, "config.txt")
            // SubstituteInFilesEnabled NOT set — gate is off
        );

        var script = IISDeployScriptBuilder.Build(action, variables);
        var result = RunPowerShell(script);

        result.ExitCode.ShouldBe(0, customMessage: $"Deploy failed: {result.StdErr}");

        File.ReadAllText(configPath).ShouldBe(originalContent,
            customMessage: "File was modified despite feature being OFF — gate is broken.");

        ctx.MarkClean();
    }

    // ── XML Configuration Transforms / XDT (Phase 7) ────────────────────────
    //
    // The deploy script's `Update-IISConfigurationTransforms` function loads
    // Microsoft.Web.XmlTransform.dll and applies XDT transforms:
    //   - Auto: `<base>.Release.config` over `<base>.config`
    //   - Auto: `<base>.{EnvironmentName}.config` over `<base>.config`
    //   - Explicit: CSV `transform.config => target.config` entries
    //
    // These tests stage real config + transform files in the WebRoot, deploy with the
    // feature enabled, and assert XDT semantics were applied (e.g. `xdt:Transform="SetAttributes"`
    // replaced the target attribute value).

    [Fact]
    public void RealIIS_ConfigurationTransforms_AutoReleaseTransform_AppliedOverBase()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;
        if (!IsMicrosoftWebXmlTransformAvailable()) return;

        using var ctx = new IISTestContext();
        var webConfigPath = Path.Combine(ctx.PhysicalPath, "web.config");
        var releaseTransformPath = Path.Combine(ctx.PhysicalPath, "web.Release.config");

        File.WriteAllText(webConfigPath,
            "<?xml version=\"1.0\"?>\n" +
            "<configuration>\n" +
            "  <appSettings>\n" +
            "    <add key=\"ApiUrl\" value=\"https://localhost-dev.example.com\" />\n" +
            "  </appSettings>\n" +
            "</configuration>\n");

        File.WriteAllText(releaseTransformPath,
            "<?xml version=\"1.0\"?>\n" +
            "<configuration xmlns:xdt=\"http://schemas.microsoft.com/XML-Document-Transform\">\n" +
            "  <appSettings>\n" +
            "    <add key=\"ApiUrl\" value=\"https://api.prod.example.com\"\n" +
            "         xdt:Transform=\"SetAttributes(value)\" xdt:Locator=\"Match(key)\" />\n" +
            "  </appSettings>\n" +
            "</configuration>\n");

        var action = BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]"),
            (Property.ConfigurationTransformsEnabled, "True"));

        var script = IISDeployScriptBuilder.Build(action);
        var result = RunPowerShell(script);

        result.ExitCode.ShouldBe(0,
            customMessage: $"XDT Release transform deploy failed.\nSTDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        var rewritten = File.ReadAllText(webConfigPath);
        rewritten.ShouldContain("value=\"https://api.prod.example.com\"",
            customMessage:
                $"XDT Release transform NOT applied — `xdt:Transform=\"SetAttributes(value)\"` should have replaced the ApiUrl value. " +
                $"File after deploy:\n{rewritten}");
        rewritten.ShouldNotContain("localhost-dev.example.com",
            customMessage: "Original (pre-transform) ApiUrl value still present — XDT didn't take effect.");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_ConfigurationTransforms_EnvironmentSpecificTransform_AppliedOverBase()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;
        if (!IsMicrosoftWebXmlTransformAvailable()) return;

        using var ctx = new IISTestContext();
        var webConfigPath = Path.Combine(ctx.PhysicalPath, "web.config");
        var stagingTransformPath = Path.Combine(ctx.PhysicalPath, "web.Staging.config");

        File.WriteAllText(webConfigPath,
            "<?xml version=\"1.0\"?>\n" +
            "<configuration>\n" +
            "  <appSettings>\n" +
            "    <add key=\"LogLevel\" value=\"Info\" />\n" +
            "  </appSettings>\n" +
            "</configuration>\n");

        File.WriteAllText(stagingTransformPath,
            "<?xml version=\"1.0\"?>\n" +
            "<configuration xmlns:xdt=\"http://schemas.microsoft.com/XML-Document-Transform\">\n" +
            "  <appSettings>\n" +
            "    <add key=\"LogLevel\" value=\"Debug\"\n" +
            "         xdt:Transform=\"SetAttributes(value)\" xdt:Locator=\"Match(key)\" />\n" +
            "  </appSettings>\n" +
            "</configuration>\n");

        var action = BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]"),
            (Property.ConfigurationTransformsEnabled, "True"),
            (Property.ConfigurationTransformsEnvironmentName, "Staging"));

        var script = IISDeployScriptBuilder.Build(action);
        var result = RunPowerShell(script);

        result.ExitCode.ShouldBe(0,
            customMessage: $"XDT Staging transform deploy failed.\nSTDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        var rewritten = File.ReadAllText(webConfigPath);
        rewritten.ShouldContain("value=\"Debug\"",
            customMessage:
                $"web.Staging.config transform NOT applied — EnvironmentName='Staging' should have triggered the env-specific overlay. " +
                $"File after deploy:\n{rewritten}");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_ConfigurationTransforms_ExplicitAdditionalTransform_AppliedOverNonStandardTarget()
    {
        // Explicit transforms via the AdditionalTransforms CSV — operators use this for
        // transforms whose source filename doesn't follow the `<base>.{name}.config` convention.
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;
        if (!IsMicrosoftWebXmlTransformAvailable()) return;

        using var ctx = new IISTestContext();
        var configPath = Path.Combine(ctx.PhysicalPath, "ConnectionStrings.config");
        var transformPath = Path.Combine(ctx.PhysicalPath, "ConnectionStrings.transform.config");

        File.WriteAllText(configPath,
            "<?xml version=\"1.0\"?>\n" +
            "<connectionStrings>\n" +
            "  <add name=\"Default\" connectionString=\"Server=localhost\" />\n" +
            "</connectionStrings>\n");

        File.WriteAllText(transformPath,
            "<?xml version=\"1.0\"?>\n" +
            "<connectionStrings xmlns:xdt=\"http://schemas.microsoft.com/XML-Document-Transform\">\n" +
            "  <add name=\"Default\" connectionString=\"Server=prod-cluster\"\n" +
            "       xdt:Transform=\"SetAttributes(connectionString)\" xdt:Locator=\"Match(name)\" />\n" +
            "</connectionStrings>\n");

        var action = BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]"),
            // Toggle off, but additional transforms present — agent still runs the rewriter
            // because of the gate condition (`enabled == "True" -or additionalTransforms is non-empty`)
            (Property.ConfigurationTransformsAdditional, "ConnectionStrings.transform.config => ConnectionStrings.config"));

        var script = IISDeployScriptBuilder.Build(action);
        var result = RunPowerShell(script);

        result.ExitCode.ShouldBe(0,
            customMessage: $"XDT explicit-transform deploy failed.\nSTDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        var rewritten = File.ReadAllText(configPath);
        rewritten.ShouldContain("connectionString=\"Server=prod-cluster\"",
            customMessage:
                $"Explicit `transform => target` did not apply. CSV parsing or non-base-file XDT path is broken. " +
                $"File after deploy:\n{rewritten}");
        rewritten.ShouldNotContain("Server=localhost",
            customMessage: "Original connectionString still present after explicit transform.");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_ConfigurationTransforms_FeatureDisabled_FileUntouched()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        var webConfigPath = Path.Combine(ctx.PhysicalPath, "web.config");
        var releaseTransformPath = Path.Combine(ctx.PhysicalPath, "web.Release.config");

        const string originalContent =
            "<?xml version=\"1.0\"?>\n" +
            "<configuration>\n" +
            "  <appSettings>\n" +
            "    <add key=\"ApiUrl\" value=\"localhost-dev\" />\n" +
            "  </appSettings>\n" +
            "</configuration>\n";
        File.WriteAllText(webConfigPath, originalContent);

        File.WriteAllText(releaseTransformPath,
            "<?xml version=\"1.0\"?>\n" +
            "<configuration xmlns:xdt=\"http://schemas.microsoft.com/XML-Document-Transform\">\n" +
            "  <appSettings>\n" +
            "    <add key=\"ApiUrl\" value=\"SHOULD_NOT_BE_APPLIED\"\n" +
            "         xdt:Transform=\"SetAttributes(value)\" xdt:Locator=\"Match(key)\" />\n" +
            "  </appSettings>\n" +
            "</configuration>\n");

        var action = BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]")
            // ConfigurationTransformsEnabled NOT set + no AdditionalTransforms = gate stays off
        );

        var script = IISDeployScriptBuilder.Build(action);
        var result = RunPowerShell(script);

        result.ExitCode.ShouldBe(0,
            customMessage: $"Deploy with XDT off failed:\n{result.StdErr}");

        var content = File.ReadAllText(webConfigPath);
        content.ShouldContain("value=\"localhost-dev\"",
            customMessage: "Base web.config value lost despite XDT feature OFF — gate is broken.");
        content.ShouldNotContain("SHOULD_NOT_BE_APPLIED",
            customMessage: "Transform applied despite feature being off — gate is broken.");

        ctx.MarkClean();
    }

    // ── Custom-script hooks (Phase 5: PreDeploy + PostDeploy) ───────────────
    //
    // Octopus's `ConfiguredScriptBehaviour` runs operator-inline scripts at 5 stages of
    // the deploy pipeline. Squid embeds the two most-used stages (PreDeploy + PostDeploy)
    // directly inside the IIS deploy script — PreDeploy runs BEFORE the IIS configure
    // dispatch, PostDeploy runs AFTER it completes (success path only — `throw` in the
    // IIS configure block aborts before PostDeploy).
    //
    // These tests verify real PowerShell execution + ordering + failure propagation against
    // real IIS. Sentinel files at known temp paths witness which scripts ran.

    [Fact]
    public void RealIIS_PreDeployScript_RunsBeforeIISConfigure()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        var sentinelPath = ctx.RegisterSentinelPath("predeploy");

        // PreDeploy writes a sentinel file. After deploy, both the sentinel AND the IIS site
        // must exist — proving PreDeploy ran AND the deploy still proceeded normally.
        var script = IISDeployScriptBuilder.Build(BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]"),
            (Property.CustomScriptsPreDeploy, $"Set-Content -Path '{sentinelPath}' -Value 'predeploy-ran'")));

        var result = RunPowerShell(script);
        result.ExitCode.ShouldBe(0,
            customMessage: $"Deploy with PreDeploy script failed.\nSTDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        File.Exists(sentinelPath).ShouldBeTrue(
            customMessage: $"PreDeploy sentinel '{sentinelPath}' was not created — PreDeploy didn't run. " +
                          "Check the IIS PS1's PreDeploy hook block at the bottom of the file.");

        File.ReadAllText(sentinelPath).Trim().ShouldBe("predeploy-ran");

        // IIS site must also exist — PreDeploy succeeded, deploy proceeded.
        PowerShellSingleLine($"if (Get-Website -Name '{ctx.SiteName}' -ErrorAction SilentlyContinue) {{ 'true' }} else {{ 'false' }}")
            .ShouldBe("true", customMessage: "Site missing after PreDeploy succeeded — deploy aborted unexpectedly.");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_PostDeployScript_RunsAfterIISConfigure_SeesSiteInMetabase()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        var sentinelPath = ctx.RegisterSentinelPath("postdeploy");

        // PostDeploy queries Get-Website and writes the site state to the sentinel.
        // If PostDeploy ran AFTER IIS configure, the site MUST be in the metabase
        // and `(Get-Website).State` returns "Started".
        var script = IISDeployScriptBuilder.Build(BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]"),
            (Property.StartApplicationPool, "True"),
            (Property.StartWebSite, "True"),
            (Property.CustomScriptsPostDeploy,
                $"$site = Get-Website -Name '{ctx.SiteName}'; " +
                $"Set-Content -Path '{sentinelPath}' -Value $site.State")));

        var result = RunPowerShell(script);
        result.ExitCode.ShouldBe(0,
            customMessage: $"Deploy with PostDeploy script failed.\nSTDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        File.Exists(sentinelPath).ShouldBeTrue(
            customMessage: $"PostDeploy sentinel '{sentinelPath}' was not created — PostDeploy didn't run. " +
                          "Check the IIS PS1's PostDeploy hook block at the bottom of the file.");

        var observedState = File.ReadAllText(sentinelPath).Trim();
        observedState.ShouldBe("Started",
            customMessage: $"PostDeploy observed site state='{observedState}', expected 'Started'. " +
                          "Either PostDeploy ran BEFORE the IIS configure (impossible per script order), " +
                          "OR Start-WebSite didn't fire. Check IIS PS1 deploy logic.");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_PreDeployScriptFailure_AbortsBeforeIISConfigure()
    {
        // Failure propagation: PreDeploy throws → deploy must abort with non-zero exit
        // AND the IIS site must NOT be created (the configure block never runs).
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        var postDeploySentinel = ctx.RegisterSentinelPath("postdeploy-should-not-fire");

        var script = IISDeployScriptBuilder.Build(BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]"),
            (Property.CustomScriptsPreDeploy, "throw 'intentional pre-deploy failure'"),
            (Property.CustomScriptsPostDeploy,
                $"Set-Content -Path '{postDeploySentinel}' -Value 'this-should-never-fire'")));

        var result = RunPowerShell(script);

        result.ExitCode.ShouldNotBe(0,
            customMessage: $"Deploy with PreDeploy throw unexpectedly succeeded. " +
                          $"STDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        // IIS site MUST NOT exist — the configure block never ran.
        PowerShellSingleLine($"if (Get-Website -Name '{ctx.SiteName}' -ErrorAction SilentlyContinue) {{ 'true' }} else {{ 'false' }}")
            .ShouldBe("false",
                customMessage: $"Site '{ctx.SiteName}' was created despite PreDeploy throwing — the abort path is broken.");

        // PostDeploy MUST NOT have fired — the throw should have stopped script execution.
        File.Exists(postDeploySentinel).ShouldBeFalse(
            customMessage: $"PostDeploy sentinel '{postDeploySentinel}' exists — PostDeploy ran after a PreDeploy throw. " +
                          "Script execution should stop at the throw.");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_PreDeployMultiLineScript_AllStatementsExecute()
    {
        // Multi-line operator scripts MUST preserve newlines through the base64 round-trip.
        // Single-quote single-line escape would have collapsed `\n` to space and broken this.
        // This test writes TWO distinct sentinel files from a multi-line PreDeploy script.
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        var sentinel1 = ctx.RegisterSentinelPath("multiline-line1");
        var sentinel2 = ctx.RegisterSentinelPath("multiline-line2");

        var multiLineScript =
            $"Set-Content -Path '{sentinel1}' -Value 'first-line-ran'\n" +
            $"Start-Sleep -Milliseconds 50\n" +
            $"Set-Content -Path '{sentinel2}' -Value 'second-line-ran'";

        var script = IISDeployScriptBuilder.Build(BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]"),
            (Property.CustomScriptsPreDeploy, multiLineScript)));

        var result = RunPowerShell(script);
        result.ExitCode.ShouldBe(0,
            customMessage: $"Multi-line PreDeploy deploy failed.\nSTDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        File.Exists(sentinel1).ShouldBeTrue(
            customMessage: $"First sentinel '{sentinel1}' missing — first line of multi-line script didn't run. " +
                          "Likely base64 round-trip lost newlines (would have made statement 1 and 2 collide).");

        File.Exists(sentinel2).ShouldBeTrue(
            customMessage: $"Second sentinel '{sentinel2}' missing — second line of multi-line script didn't run.");

        File.ReadAllText(sentinel1).Trim().ShouldBe("first-line-ran");
        File.ReadAllText(sentinel2).Trim().ShouldBe("second-line-ran");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_PreAndPostDeployStopThenStartAppPool_RealisticOperatorWorkflow()
    {
        // Realistic operator workflow: PreDeploy stops the app pool (releases file locks for
        // a subsequent file copy), PostDeploy starts it again. This is the canonical Octopus
        // IIS deploy pattern. After the full deploy, the pool MUST be in "Started" state.
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();

        // First deploy: create the site and pool, with both Pre + Post scripts wired.
        // PreDeploy: Stop-WebAppPool '#{PoolName}' — Stop is no-op for a non-existent pool on
        //   first deploy (handled gracefully via -ErrorAction SilentlyContinue).
        // PostDeploy: explicit `Start-WebAppPool '#{PoolName}'` to ensure pool is running.
        // Note: the IIS configure script itself starts the pool via StartApplicationPool=True,
        // so PostDeploy here is documenting + asserting the operator's explicit pattern.
        var script = IISDeployScriptBuilder.Build(BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]"),
            (Property.StartApplicationPool, "True"),
            (Property.StartWebSite, "True"),
            (Property.CustomScriptsPreDeploy, $"Stop-WebAppPool -Name '{ctx.PoolName}' -ErrorAction SilentlyContinue"),
            (Property.CustomScriptsPostDeploy, $"Start-WebAppPool -Name '{ctx.PoolName}' -ErrorAction Stop")));

        var result = RunPowerShell(script);
        result.ExitCode.ShouldBe(0,
            customMessage:
                $"Realistic Pre+Post workflow failed.\nSTDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}\n\n" +
                $"Manually inspect via `Get-WebAppPool -Name {ctx.PoolName}`.");

        // Pool MUST be in Started state after the workflow.
        var poolState = PowerShellSingleLine($"(Get-WebAppPool -Name '{ctx.PoolName}').State");
        poolState.ShouldBe("Started",
            customMessage: $"After Pre/Post Stop-Start workflow, pool state='{poolState}', expected 'Started'. " +
                          "PostDeploy `Start-WebAppPool` either didn't run or failed silently.");

        ctx.MarkClean();
    }

    // ── WebApplication + VirtualDirectory sub-features (Phase 4) ────────────
    //
    // Octopus's PS1 supports three deployment-type toggles in one action:
    //  - `Squid.Action.IISWebSite.CreateOrUpdateWebSite` — parent website
    //  - `Squid.Action.IISWebSite.WebApplication.CreateOrUpdate` — child WebApp under existing parent
    //  - `Squid.Action.IISWebSite.VirtualDirectory.CreateOrUpdate` — child VirtDir under existing parent
    //
    // The PS1 evaluates child branches (VirtDir at line 311, WebApp at line 360) BEFORE
    // the WebSite branch (line 426). `Assert-WebsiteExists` throws fatally if the parent
    // is missing, so the Octopus contract is: parent must already exist (typically from a
    // prior deploy step). These tests follow that contract — each test pre-deploys the
    // parent via a first `Build(...)` call with only `CreateOrUpdateWebSite=True`, then a
    // second deploy activates the child sub-feature.

    [Fact]
    public void RealIIS_WebApplication_CreatedUnderExistingParentSite()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        ctx.EnsureWebAppPhysicalPathExists();

        DeployParentSite(ctx);

        var script = IISDeployScriptBuilder.Build(BuildAction(
            (Property.WebApplicationCreateOrUpdate, "True"),
            (Property.WebApplicationWebSiteName, ctx.SiteName),
            (Property.WebApplicationVirtualPath, ctx.WebAppVirtualPath),
            (Property.WebApplicationPhysicalPath, ctx.WebAppPhysicalPath),
            (Property.WebApplicationApplicationPoolName, ctx.WebAppPoolName),
            (Property.WebApplicationApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.WebApplicationApplicationPoolFrameworkVersion, "v4.0"),
            (Property.StartApplicationPool, "True")));

        var result = RunPowerShell(script);
        result.ExitCode.ShouldBe(0,
            customMessage:
                $"WebApplication deploy failed. STDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}\n\n" +
                $"Manually: `Get-WebApplication -Site '{ctx.SiteName}' -Name '{ctx.WebAppVirtualPath.TrimStart('/')}'`.");

        // The metabase must show a node of ElementTagName='application' at the configured path.
        var sitePath = $"IIS:\\Sites\\{ctx.SiteName}{ctx.WebAppVirtualPath}";
        var elementType = ReadIISElementType(sitePath);
        elementType.ShouldBe("application",
            customMessage:
                $"Expected an IIS application at '{sitePath}', got ElementTagName='{elementType}'. " +
                $"The PS1's `New-Item -type Application` line 392 may have run with the wrong path or " +
                $"silently failed. Manually: `Get-Item '{sitePath}'`.");

        var physicalPath = ReadIISPhysicalPath(sitePath);
        physicalPath.ShouldBe(ctx.WebAppPhysicalPath,
            customMessage:
                $"WebApplication physicalPath mismatch. Configured='{ctx.WebAppPhysicalPath}', " +
                $"actual='{physicalPath}'.");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_VirtualDirectory_CreatedUnderExistingParentSite()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        ctx.EnsureVirtDirPhysicalPathExists();

        DeployParentSite(ctx);

        var script = IISDeployScriptBuilder.Build(BuildAction(
            (Property.VirtualDirectoryCreateOrUpdate, "True"),
            (Property.VirtualDirectoryWebSiteName, ctx.SiteName),
            (Property.VirtualDirectoryVirtualPath, ctx.VirtDirVirtualPath),
            (Property.VirtualDirectoryPhysicalPath, ctx.VirtDirPhysicalPath)));

        var result = RunPowerShell(script);
        result.ExitCode.ShouldBe(0,
            customMessage:
                $"VirtualDirectory deploy failed. STDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}\n\n" +
                $"Manually: `Get-WebVirtualDirectory -Site '{ctx.SiteName}' -Name '{ctx.VirtDirVirtualPath.TrimStart('/')}'`.");

        var sitePath = $"IIS:\\Sites\\{ctx.SiteName}{ctx.VirtDirVirtualPath}";
        var elementType = ReadIISElementType(sitePath);
        elementType.ShouldBe("virtualDirectory",
            customMessage:
                $"Expected an IIS virtualDirectory at '{sitePath}', got ElementTagName='{elementType}'. " +
                $"The PS1's `New-Item -type VirtualDirectory` line 334 may have run with the wrong path. " +
                $"Manually: `Get-Item '{sitePath}'`.");

        var physicalPath = ReadIISPhysicalPath(sitePath);
        physicalPath.ShouldBe(ctx.VirtDirPhysicalPath,
            customMessage:
                $"VirtualDirectory physicalPath mismatch. Configured='{ctx.VirtDirPhysicalPath}', " +
                $"actual='{physicalPath}'.");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_WebApplication_FrameworkVersionDifferentFromParent_AppliedIndependently()
    {
        // A common production pattern: parent site runs v4.0 for the main app, child WebApp
        // runs v2.0 (or No Managed Code) for a legacy sub-app. The two app pools must
        // hold their own framework values independently. PS1 line 375 reads the WebApp's
        // OWN ApplicationPoolFrameworkVersion property — different from the parent's.
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        ctx.EnsureWebAppPhysicalPathExists();

        DeployParentSite(ctx);   // parent uses v4.0 (set in DeployParentSite)

        var script = IISDeployScriptBuilder.Build(BuildAction(
            (Property.WebApplicationCreateOrUpdate, "True"),
            (Property.WebApplicationWebSiteName, ctx.SiteName),
            (Property.WebApplicationVirtualPath, ctx.WebAppVirtualPath),
            (Property.WebApplicationPhysicalPath, ctx.WebAppPhysicalPath),
            (Property.WebApplicationApplicationPoolName, ctx.WebAppPoolName),
            (Property.WebApplicationApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.WebApplicationApplicationPoolFrameworkVersion, "v2.0"),
            (Property.StartApplicationPool, "True")));

        var result = RunPowerShell(script);
        result.ExitCode.ShouldBe(0, $"WebApp framework-override deploy failed: {result.StdErr}");

        // Verify the WebApp's pool has v2.0, parent pool still has v4.0 — independence.
        var parentPoolFramework = PowerShellSingleLine(
            $"(Get-ItemProperty IIS:\\AppPools\\{ctx.PoolName} -Name managedRuntimeVersion).Value");
        var webAppPoolFramework = PowerShellSingleLine(
            $"(Get-ItemProperty IIS:\\AppPools\\{ctx.WebAppPoolName} -Name managedRuntimeVersion).Value");

        parentPoolFramework.ShouldBe("v4.0",
            customMessage: $"Parent pool framework changed unexpectedly. Should still be v4.0, was '{parentPoolFramework}'.");

        webAppPoolFramework.ShouldBe("v2.0",
            customMessage:
                $"WebApp pool framework not set to v2.0, was '{webAppPoolFramework}'. " +
                $"PS1 may be reading the parent's framework instead of the WebApp's own property.");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_WebApplication_Redeploy_UpdatesPhysicalPath()
    {
        // Idempotence + change-detection: redeploy the same WebApp with a different physical
        // path. PS1's "already exists, no need to create it" branch (line 396) should be hit
        // on the second pass, but `Set-Path` (line 413) MUST still update the physicalPath.
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        ctx.EnsureWebAppPhysicalPathExists();

        DeployParentSite(ctx);

        var script1 = IISDeployScriptBuilder.Build(BuildAction(
            (Property.WebApplicationCreateOrUpdate, "True"),
            (Property.WebApplicationWebSiteName, ctx.SiteName),
            (Property.WebApplicationVirtualPath, ctx.WebAppVirtualPath),
            (Property.WebApplicationPhysicalPath, ctx.WebAppPhysicalPath),
            (Property.WebApplicationApplicationPoolName, ctx.WebAppPoolName),
            (Property.WebApplicationApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.WebApplicationApplicationPoolFrameworkVersion, "v4.0")));

        RunPowerShell(script1).ExitCode.ShouldBe(0, "First WebApp deploy must succeed.");

        // Second deploy: new physical path. The directory must exist for IIS to accept it.
        var newPhysicalPath = Path.Combine(Path.GetTempPath(), $"squid-iis-webapp-rotated-{Guid.NewGuid():N}");
        Directory.CreateDirectory(newPhysicalPath);
        ctx.RegisterTempDirForCleanup(newPhysicalPath);

        var script2 = IISDeployScriptBuilder.Build(BuildAction(
            (Property.WebApplicationCreateOrUpdate, "True"),
            (Property.WebApplicationWebSiteName, ctx.SiteName),
            (Property.WebApplicationVirtualPath, ctx.WebAppVirtualPath),
            (Property.WebApplicationPhysicalPath, newPhysicalPath),
            (Property.WebApplicationApplicationPoolName, ctx.WebAppPoolName),
            (Property.WebApplicationApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.WebApplicationApplicationPoolFrameworkVersion, "v4.0")));

        var result2 = RunPowerShell(script2);
        result2.ExitCode.ShouldBe(0, $"WebApp redeploy failed: {result2.StdErr}");

        var sitePath = $"IIS:\\Sites\\{ctx.SiteName}{ctx.WebAppVirtualPath}";
        var physicalPath = ReadIISPhysicalPath(sitePath);
        physicalPath.ShouldBe(newPhysicalPath,
            customMessage:
                $"WebApp physicalPath not updated after redeploy. Expected='{newPhysicalPath}', " +
                $"actual='{physicalPath}'. PS1's `Set-Path` (line 413) didn't run or ran with the wrong target.");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_WebApplicationAndVirtualDirectory_BothInSingleAction_BothCreated()
    {
        // Composite deploy: the same Squid action sets BOTH WebApplication.CreateOrUpdate
        // AND VirtualDirectory.CreateOrUpdate to True under the same parent site. Operators
        // use this for /api (WebApp) + /static (VirtDir) layouts in one Squid step.
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        ctx.EnsureWebAppPhysicalPathExists();
        ctx.EnsureVirtDirPhysicalPathExists();

        DeployParentSite(ctx);

        var script = IISDeployScriptBuilder.Build(BuildAction(
            // WebApp leg
            (Property.WebApplicationCreateOrUpdate, "True"),
            (Property.WebApplicationWebSiteName, ctx.SiteName),
            (Property.WebApplicationVirtualPath, ctx.WebAppVirtualPath),
            (Property.WebApplicationPhysicalPath, ctx.WebAppPhysicalPath),
            (Property.WebApplicationApplicationPoolName, ctx.WebAppPoolName),
            (Property.WebApplicationApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.WebApplicationApplicationPoolFrameworkVersion, "v4.0"),
            // VirtDir leg
            (Property.VirtualDirectoryCreateOrUpdate, "True"),
            (Property.VirtualDirectoryWebSiteName, ctx.SiteName),
            (Property.VirtualDirectoryVirtualPath, ctx.VirtDirVirtualPath),
            (Property.VirtualDirectoryPhysicalPath, ctx.VirtDirPhysicalPath)));

        var result = RunPowerShell(script);
        result.ExitCode.ShouldBe(0,
            customMessage:
                $"Composite WebApp+VirtDir deploy failed. STDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        ReadIISElementType($"IIS:\\Sites\\{ctx.SiteName}{ctx.WebAppVirtualPath}").ShouldBe("application",
            customMessage: $"WebApp leg of composite deploy didn't create the application node.");
        ReadIISElementType($"IIS:\\Sites\\{ctx.SiteName}{ctx.VirtDirVirtualPath}").ShouldBe("virtualDirectory",
            customMessage: $"VirtDir leg of composite deploy didn't create the virtualDirectory node.");

        ctx.MarkClean();
    }

    [Fact]
    public void RealIIS_WebApplication_NoParentSite_FailsWithActionableError()
    {
        // PS1's `Assert-WebsiteExists` (line 282) throws when the configured parent
        // WebSite doesn't exist. The error message guides the operator to add a
        // pre-step that creates the parent. This test pins that the error path
        // produces the expected actionable text — operators rely on grepping for
        // the phrase to triage misconfigured deploy orders.
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new IISTestContext();
        ctx.EnsureWebAppPhysicalPathExists();
        // NOTE: NOT calling DeployParentSite — the parent doesn't exist.

        var script = IISDeployScriptBuilder.Build(BuildAction(
            (Property.WebApplicationCreateOrUpdate, "True"),
            (Property.WebApplicationWebSiteName, ctx.SiteName),
            (Property.WebApplicationVirtualPath, ctx.WebAppVirtualPath),
            (Property.WebApplicationPhysicalPath, ctx.WebAppPhysicalPath),
            (Property.WebApplicationApplicationPoolName, ctx.WebAppPoolName),
            (Property.WebApplicationApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.WebApplicationApplicationPoolFrameworkVersion, "v4.0")));

        var result = RunPowerShell(script);

        result.ExitCode.ShouldNotBe(0,
            customMessage:
                $"WebApp deploy without a parent site unexpectedly SUCCEEDED. PS1's `Assert-WebsiteExists` " +
                $"line 282 should have thrown. STDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        var combinedOutput = result.StdOut + result.StdErr;
        combinedOutput.ShouldContain("does not exist in IIS",
            customMessage:
                "Missing-parent error didn't contain the actionable 'does not exist in IIS' phrase. " +
                "Operators grep for this exact text. If renamed in PS1 update this test in lockstep.");

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
        private readonly List<string> _localUsersToClean = new();
        private readonly List<string> _sentinelFilesToClean = new();
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

        /// <summary>
        /// Generates a unique sentinel-file path under the OS temp dir and registers it for
        /// removal during <see cref="Dispose"/>. Used by Phase 5 custom-script tests to witness
        /// that the operator's PreDeploy / PostDeploy hook actually ran (sentinel exists =
        /// script body executed; file content = what the script wrote).
        /// </summary>
        /// <param name="suffix">Short label to disambiguate concurrent sentinels in the same test.</param>
        public string RegisterSentinelPath(string suffix)
        {
            var path = Path.Combine(Path.GetTempPath(), $"squid-iis-sentinel-{_suffix}-{suffix}.txt");
            _sentinelFilesToClean.Add(path);
            return path;
        }

        /// <summary>
        /// Stages the WebApplication physical-path directory and registers it for cleanup.
        /// Used by Phase 4 tests that activate the `Squid.Action.IISWebSite.WebApplication.*`
        /// sub-feature. Calling this is optional — Phase 1-3.5 tests don't touch the WebApp
        /// state and skip this entirely.
        /// </summary>
        public void EnsureWebAppPhysicalPathExists()
        {
            if (!Directory.Exists(WebAppPhysicalPath))
                Directory.CreateDirectory(WebAppPhysicalPath);
            if (!_tempDirsToClean.Contains(WebAppPhysicalPath))
                _tempDirsToClean.Add(WebAppPhysicalPath);
        }

        /// <summary>
        /// Stages the VirtualDirectory physical-path directory and registers it for cleanup.
        /// </summary>
        public void EnsureVirtDirPhysicalPathExists()
        {
            if (!Directory.Exists(VirtDirPhysicalPath))
                Directory.CreateDirectory(VirtDirPhysicalPath);
            if (!_tempDirsToClean.Contains(VirtDirPhysicalPath))
                _tempDirsToClean.Add(VirtDirPhysicalPath);
        }

        public string SiteName { get; }
        public string PoolName { get; }
        public string PhysicalPath { get; }
        public string HttpPort { get; }
        public string HttpsPort { get; }

        // Phase 4 — child WebApplication + VirtualDirectory derive their state from
        // the same per-test GUID suffix so concurrent CI runs stay isolated. Cleanup
        // is handled by Remove-Website cascading to children; the separate WebApp
        // pool needs its own Remove-WebAppPool call (registered below).
        public string WebAppVirtualPath => $"/webapp-{_suffix}";
        public string WebAppPhysicalPath => Path.Combine(Path.GetTempPath(), $"squid-iis-webapp-{_suffix}");
        public string WebAppPoolName => $"{PoolName}-webapp";
        public string VirtDirVirtualPath => $"/virtdir-{_suffix}";
        public string VirtDirPhysicalPath => Path.Combine(Path.GetTempPath(), $"squid-iis-virtdir-{_suffix}");

        public void RegisterTempDirForCleanup(string path) => _tempDirsToClean.Add(path);

        /// <summary>
        /// Allocates an additional free localhost port (separate from <see cref="HttpPort"/> /
        /// <see cref="HttpsPort"/>). Used by the ExistingBindings=Merge test which needs a
        /// pre-existing binding port AND the deploy-time binding port to coexist.
        /// </summary>
        public string PickAdditionalFreePort() => PickFreePort();

        /// <summary>
        /// Creates a local Windows user account via <c>net user /add</c> and registers it
        /// for removal in <see cref="Dispose"/>. The password satisfies the default Windows
        /// local-policy complexity (≥ 8 chars, mixed case, digit, symbol).
        ///
        /// <para>Used by the <c>SpecificUser</c> app-pool identity test. The account is
        /// created but NOT granted SeBatchLogonRight, so the resulting pool cannot actually
        /// START — that's outside scope; the test asserts the metabase write only.</para>
        /// </summary>
        public (string UserName, string Password) StageLocalWindowsUser()
        {
            var userName = $"squid-iis-{_suffix}";
            // Strong password: lower, upper, digit, symbol, ≥ 8 chars. Uses GUID for entropy.
            var password = $"Sq!{Guid.NewGuid():N}";

            var result = RunPowerShell($"& net user '{userName}' '{password}' /add");
            if (result.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Failed to stage local user '{userName}'. ExitCode={result.ExitCode}, " +
                    $"StdOut='{result.StdOut}', StdErr='{result.StdErr}'.");

            _localUsersToClean.Add(userName);
            return (userName, password);
        }

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
                // Remove-Website cascades to remove child applications + virtual directories,
                // so the WebApp/VirtDir state is implicitly cleaned up. The WebApp's own pool
                // is a separate metabase entry though — must be removed independently.
                TryPowerShell($"Remove-Website -Name '{SiteName}' -ErrorAction SilentlyContinue");
                TryPowerShell($"Remove-WebAppPool -Name '{PoolName}' -ErrorAction SilentlyContinue");
                TryPowerShell($"Remove-WebAppPool -Name '{WebAppPoolName}' -ErrorAction SilentlyContinue");
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

            // Local Windows users created by StageLocalWindowsUser. Must run AFTER the app pool
            // is gone (some teardown paths reference the user via processModel), so leave this
            // last among the OS-state cleanups.
            foreach (var user in _localUsersToClean)
                TryPowerShell($"& net user '{user}' /delete | Out-Null");

            foreach (var dir in _tempDirsToClean)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
                catch { /* best-effort */ }
            }

            // Sentinel files written by Phase 5 custom-script tests — small text files in the
            // OS temp dir. Even if every test cleans up cleanly, the OS retention policy
            // varies, so explicit removal here makes the test suite hermetic.
            foreach (var sentinelPath in _sentinelFilesToClean)
            {
                try { if (File.Exists(sentinelPath)) File.Delete(sentinelPath); }
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
        public const string EnableAnonymousAuthentication = "Squid.Action.IISWebSite.EnableAnonymousAuthentication";
        public const string EnableBasicAuthentication = "Squid.Action.IISWebSite.EnableBasicAuthentication";
        public const string EnableWindowsAuthentication = "Squid.Action.IISWebSite.EnableWindowsAuthentication";
        public const string ApplicationPoolUsername = "Squid.Action.IISWebSite.ApplicationPoolUsername";
        public const string ApplicationPoolPassword = "Squid.Action.IISWebSite.ApplicationPoolPassword";
        public const string ExistingBindings = "Squid.Action.IISWebSite.ExistingBindings";

        // Phase 5 — custom script slots (Octopus CustomScripts parity)
        public const string CustomScriptsPreDeploy = "Squid.Action.CustomScripts.PreDeploy.ps1";
        public const string CustomScriptsPostDeploy = "Squid.Action.CustomScripts.PostDeploy.ps1";

        // Phase 6 — .NET Configuration Variables (Octopus ConfigurationVariables parity)
        public const string ConfigurationVariablesEnabled = "Squid.Action.IISWebSite.ConfigurationVariables.Enabled";

        // Phase 7 — XML Configuration Transforms (Octopus ConfigurationTransforms parity / XDT)
        public const string ConfigurationTransformsEnabled = "Squid.Action.IISWebSite.ConfigurationTransforms.Enabled";
        public const string ConfigurationTransformsEnvironmentName = "Squid.Action.IISWebSite.ConfigurationTransforms.EnvironmentName";
        public const string ConfigurationTransformsAdditional = "Squid.Action.IISWebSite.ConfigurationTransforms.AdditionalTransforms";

        // Phase 8 — SubstituteInFiles (Octopus SubstituteInFiles parity)
        public const string SubstituteInFilesEnabled = "Squid.Action.IISWebSite.SubstituteInFiles.Enabled";
        public const string SubstituteInFilesTargetFiles = "Squid.Action.IISWebSite.SubstituteInFiles.TargetFiles";

        // Phase 9 — Structured Configuration Variables (Octopus JsonConfigurationVariables parity)
        public const string StructuredConfigurationVariablesEnabled = "Squid.Action.IISWebSite.StructuredConfigurationVariables.Enabled";
        public const string StructuredConfigurationVariablesTargets = "Squid.Action.IISWebSite.StructuredConfigurationVariables.Targets";

        // Phase 10 — Package extraction (operator-staged .zip / .nupkg)
        public const string PackageSourcePath = "Squid.Action.IISWebSite.Package.SourcePath";
        public const string PackageExtractTo = "Squid.Action.IISWebSite.Package.ExtractTo";
        public const string PackagePurgeBeforeExtract = "Squid.Action.IISWebSite.Package.PurgeBeforeExtract";

        // Phase 4 — WebApplication sub-feature
        public const string WebApplicationCreateOrUpdate = "Squid.Action.IISWebSite.WebApplication.CreateOrUpdate";
        public const string WebApplicationWebSiteName = "Squid.Action.IISWebSite.WebApplication.WebSiteName";
        public const string WebApplicationVirtualPath = "Squid.Action.IISWebSite.WebApplication.VirtualPath";
        public const string WebApplicationPhysicalPath = "Squid.Action.IISWebSite.WebApplication.PhysicalPath";
        public const string WebApplicationApplicationPoolName = "Squid.Action.IISWebSite.WebApplication.ApplicationPoolName";
        public const string WebApplicationApplicationPoolIdentityType = "Squid.Action.IISWebSite.WebApplication.ApplicationPoolIdentityType";
        public const string WebApplicationApplicationPoolFrameworkVersion = "Squid.Action.IISWebSite.WebApplication.ApplicationPoolFrameworkVersion";

        // Phase 4 — VirtualDirectory sub-feature
        public const string VirtualDirectoryCreateOrUpdate = "Squid.Action.IISWebSite.VirtualDirectory.CreateOrUpdate";
        public const string VirtualDirectoryWebSiteName = "Squid.Action.IISWebSite.VirtualDirectory.WebSiteName";
        public const string VirtualDirectoryVirtualPath = "Squid.Action.IISWebSite.VirtualDirectory.VirtualPath";
        public const string VirtualDirectoryPhysicalPath = "Squid.Action.IISWebSite.VirtualDirectory.PhysicalPath";
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
    /// Probes whether <c>Microsoft.Web.XmlTransform.dll</c> is loadable on the host. Phase 7 XDT
    /// tests use this to skip cleanly when the DLL isn't installed (typical dev hosts without
    /// VS Build Tools). CI installs via the workflow's <c>dotnet add package Microsoft.Web.Xdt</c>
    /// step so this returns true there.
    /// </summary>
    private static bool IsMicrosoftWebXmlTransformAvailable()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            var result = RunPowerShell(
                "$ok = $false; " +
                "try { Add-Type -AssemblyName 'Microsoft.Web.XmlTransform' -ErrorAction Stop; $ok = $true } catch {}; " +
                "if (-not $ok) { " +
                "  $probe = Get-ChildItem -Path \"$env:USERPROFILE\\.nuget\\packages\\microsoft.web.xdt\" -Recurse -Filter Microsoft.Web.XmlTransform.dll -ErrorAction SilentlyContinue | Select-Object -First 1; " +
                "  if ($probe) { try { Add-Type -Path $probe.FullName; $ok = $true } catch {} } " +
                "}; " +
                "Write-Host -NoNewline ($ok.ToString())");
            return result.ExitCode == 0 && result.StdOut.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Probes whether a specific Windows-feature sub-module is installed (e.g. <c>Web-Basic-Auth</c>,
    /// <c>Web-Windows-Auth</c>). Returns false on non-Windows or if the cmdlet fails. Phase 3
    /// auth tests use this to skip cleanly on hosts where the required IIS auth module isn't
    /// present — `appcmd set config /section:basicAuthentication` errors out with a "section
    /// declaration is missing" message otherwise.
    /// </summary>
    private static bool IsIISFeatureInstalled(string featureName)
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            var result = RunPowerShell($"(Get-WindowsFeature {featureName} -ErrorAction SilentlyContinue).Installed");
            return result.ExitCode == 0 && result.StdOut.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Deploys a parent WebSite via a standalone <c>Build(...)</c> call with only
    /// <c>CreateOrUpdateWebSite=True</c>. Phase 4 tests need a parent site to exist BEFORE
    /// activating the WebApplication or VirtualDirectory sub-features (PS1 <c>Assert-WebsiteExists</c>
    /// throws otherwise). Helper consolidates the parent-bootstrap so each Phase 4 test reads
    /// as "deploy parent; then deploy child; assert child" without duplicating the parent setup.
    /// </summary>
    private static void DeployParentSite(IISTestContext ctx)
    {
        var script = IISDeployScriptBuilder.Build(BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings,
                "[{\"protocol\":\"http\",\"port\":\"" + ctx.HttpPort + "\",\"host\":\"\"," +
                "\"ipAddress\":\"*\",\"enabled\":true}]"),
            (Property.StartApplicationPool, "True"),
            (Property.StartWebSite, "True")));

        var result = RunPowerShell(script);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Parent-site bootstrap for Phase 4 test failed (site '{ctx.SiteName}'). " +
                $"ExitCode={result.ExitCode}, StdOut='{result.StdOut}', StdErr='{result.StdErr}'.");
    }

    /// <summary>
    /// Reads <c>(Get-Item).ElementTagName</c> for an IIS metabase path. Returns
    /// <c>application</c>, <c>virtualDirectory</c>, <c>site</c>, etc. — used by Phase 4
    /// tests to verify the script created a node of the expected type.
    /// </summary>
    private static string ReadIISElementType(string sitePath) =>
        PowerShellSingleLine($"(Get-Item '{sitePath}' -ErrorAction SilentlyContinue).ElementTagName");

    /// <summary>
    /// Reads <c>(Get-Item).physicalPath</c> for an IIS metabase path. Phase 4 tests use this
    /// to verify the WebApp / VirtDir node's disk-path matches what was configured.
    /// </summary>
    private static string ReadIISPhysicalPath(string sitePath) =>
        PowerShellSingleLine($"(Get-Item '{sitePath}' -ErrorAction SilentlyContinue).physicalPath");

    /// <summary>
    /// Reads the <c>enabled</c> attribute of the named auth section from the site's effective
    /// IIS config via the same <c>appcmd.exe</c> tool the PS1 used to set it. Round-tripping
    /// through appcmd (rather than `Get-WebConfigurationProperty`) ensures we see the exact
    /// value the deploy committed at apphost level, not an inherited/overridden one.
    /// </summary>
    /// <param name="siteName">IIS site name.</param>
    /// <param name="sectionName">One of <c>anonymousAuthentication</c>, <c>basicAuthentication</c>,
    /// <c>windowsAuthentication</c>.</param>
    /// <returns><c>"true"</c> or <c>"false"</c> as appcmd emits them (lowercase XML attr style),
    /// or a diagnostic string if no match.</returns>
    private static string ReadAuthEnabledFlag(string siteName, string sectionName)
    {
        var ps =
            $"$output = & \"$env:SystemRoot\\system32\\inetsrv\\appcmd.exe\" list config '{siteName}' " +
            $"-section:system.webServer/security/authentication/{sectionName}; " +
            $"$output | Out-String";
        var output = RunPowerShell(ps).StdOut;

        // Parse `<sectionName ... enabled="X" ... />` from the XML output.
        var pattern = $"<{System.Text.RegularExpressions.Regex.Escape(sectionName)}\\b[^>]*\\benabled=\"(?<v>[^\"]+)\"";
        var match = System.Text.RegularExpressions.Regex.Match(output, pattern);

        return match.Success
            ? match.Groups["v"].Value
            : $"(no '<{sectionName} ... enabled=...' found in appcmd output: {output.Trim()})";
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
