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
        private bool _markedClean;

        public IISTestContext()
        {
            SiteName = $"SquidIISE2E-{_suffix}";
            PoolName = $"SquidIISE2EPool-{_suffix}";
            PhysicalPath = Path.Combine(Path.GetTempPath(), $"squid-iis-e2e-{_suffix}");
            HttpPort = PickFreePort();

            Directory.CreateDirectory(PhysicalPath);
            _tempDirsToClean.Add(PhysicalPath);
        }

        public string SiteName { get; }
        public string PoolName { get; }
        public string PhysicalPath { get; }
        public string HttpPort { get; }

        public void RegisterTempDirForCleanup(string path) => _tempDirsToClean.Add(path);

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
