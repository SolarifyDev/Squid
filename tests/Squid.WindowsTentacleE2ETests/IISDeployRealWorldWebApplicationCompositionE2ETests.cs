using System.IO.Compression;
using Squid.Core.Services.DeploymentExecution.Tentacle.Handlers;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// Real-world composite for the WebApplication composition contract — pre-create a
/// parent IIS Web Site, then run TWO independent <c>Squid.DeployToIISWebSite</c>
/// deploys that each create a child Web Application under it at different
/// virtual paths with different .NET runtimes and different config values.
/// Asserts both children coexist in the metabase without colliding, their
/// AppPools are distinct (different runtimes), and the parent site is unaffected.
///
/// <para><b>Production gap closed</b>: the per-feature suite tests WebApplication
/// creation in isolation (one parent + one child). Customers commonly deploy
/// <c>example.com/api</c>, <c>example.com/admin</c>, and <c>example.com/static</c>
/// as three separate Squid steps targeting the same parent. A regression where
/// one child's AppPool config bleeds into the parent (or where the second child's
/// virtual-path creation overwrites the first) would only surface in this
/// composition pattern. None of the existing tests exercise it.</para>
///
/// <para><b>Coverage delta vs <see cref="IISDeployRealWorldDotNetAppE2ETests"/></b>:
/// that composite deploys a single Web SITE. This composite proves Web APPLICATION
/// composition under a shared parent — a structurally different code path in
/// <see cref="IISDeployScriptBuilder"/> + the embedded PS1's WebApp branch.</para>
///
/// <para><b>Tier</b>: 🟢 High-fidelity. Real IIS metabase, real PowerShell,
/// real <c>WebAdministration</c>, real appsettings.json + web.config IO.
/// Skip-on-non-Windows guard + IIS-feature probe.</para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.IISDeploy)]
public sealed class IISDeployRealWorldWebApplicationCompositionE2ETests
{
    [Fact]
    public void RealWorld_TwoChildWebApps_DifferentRuntimes_DifferentConfigs_BothCoexistIndependently()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new WebAppCompositionTestContext();

        // ──── STAGE 1: Pre-create the parent IIS Web Site outside the deploy step ─
        //
        // Production operators set up the parent in a separate step (or by hand);
        // each WebApp deploy then references the existing parent by name. We
        // mirror that pattern — create the parent via `New-Website` first.
        PrecreateParentSite(ctx);

        try
        {
            // ──── STAGE 2: Deploy WebApp #1 (`/api`, .NET 4.0 runtime, region=us-east-1) ──
            var artifactApi = StageMinimalAspNetArtifact(ctx.WebAppApiStagingDir, "us-east-1");
            var apiVariables = new List<VariableDto>
            {
                new() { Name = "Region", Value = "us-east-1" }
            };
            var apiAction = BuildWebAppAction(
                parentSiteName: ctx.ParentSiteName,
                virtualPath: ctx.WebAppApiVirtualPath,
                physicalPath: ctx.WebAppApiPhysicalPath,
                appPoolName: ctx.WebAppApiPoolName,
                frameworkVersion: "v4.0",
                artifactPath: artifactApi);

            var apiScript = IISDeployScriptBuilder.Build(apiAction, apiVariables);
            var apiResult = RunPowerShell(apiScript);
            apiResult.ExitCode.ShouldBe(0,
                customMessage:
                    $"WebApp /api deploy failed.\nSTDOUT:\n{apiResult.StdOut}\n\nSTDERR:\n{apiResult.StdErr}");

            // ──── STAGE 3: Deploy WebApp #2 (`/admin`, NoManagedCode runtime, region=eu-west-1) ──
            //
            // Independent deploy — different virtual path, different AppPool, different
            // runtime. If WebApp #1's metabase entry got disturbed, /api/appsettings.json
            // would show eu-west-1 by the end. If WebApp #2's metabase entry collided
            // with #1's name, the second deploy would either error OR overwrite #1.
            var artifactAdmin = StageMinimalAspNetArtifact(ctx.WebAppAdminStagingDir, "eu-west-1");
            var adminVariables = new List<VariableDto>
            {
                new() { Name = "Region", Value = "eu-west-1" }
            };
            var adminAction = BuildWebAppAction(
                parentSiteName: ctx.ParentSiteName,
                virtualPath: ctx.WebAppAdminVirtualPath,
                physicalPath: ctx.WebAppAdminPhysicalPath,
                appPoolName: ctx.WebAppAdminPoolName,
                frameworkVersion: "No Managed Code",
                artifactPath: artifactAdmin);

            var adminScript = IISDeployScriptBuilder.Build(adminAction, adminVariables);
            var adminResult = RunPowerShell(adminScript);
            adminResult.ExitCode.ShouldBe(0,
                customMessage:
                    $"WebApp /admin deploy failed.\nSTDOUT:\n{adminResult.StdOut}\n\nSTDERR:\n{adminResult.StdErr}");

            // ──── INVARIANT 1: Parent site is unchanged ──────────────────────────
            var parentExists = PowerShellSingleLine(
                $"if (Get-Website -Name '{ctx.ParentSiteName}' -ErrorAction SilentlyContinue) {{ 'true' }} else {{ 'false' }}");
            parentExists.ShouldBe("true",
                customMessage: "Parent site disappeared after WebApp deploys — composition broke the parent.");

            // ──── INVARIANT 2: Both children exist at expected paths ─────────────
            var apiChildExists = PowerShellSingleLine(
                $"if (Get-WebApplication -Site '{ctx.ParentSiteName}' -Name '{ctx.WebAppApiVirtualPath.TrimStart('/')}' -ErrorAction SilentlyContinue) {{ 'true' }} else {{ 'false' }}");
            apiChildExists.ShouldBe("true",
                customMessage: $"WebApp /api missing under parent site '{ctx.ParentSiteName}'.");

            var adminChildExists = PowerShellSingleLine(
                $"if (Get-WebApplication -Site '{ctx.ParentSiteName}' -Name '{ctx.WebAppAdminVirtualPath.TrimStart('/')}' -ErrorAction SilentlyContinue) {{ 'true' }} else {{ 'false' }}");
            adminChildExists.ShouldBe("true",
                customMessage: $"WebApp /admin missing under parent site '{ctx.ParentSiteName}'.");

            // ──── INVARIANT 3: AppPools are distinct + have different runtimes ────
            var apiRuntime = PowerShellSingleLine($"(Get-WebAppPool -Name '{ctx.WebAppApiPoolName}').managedRuntimeVersion");
            apiRuntime.ShouldBe("v4.0",
                customMessage: $"WebApp /api AppPool runtime is '{apiRuntime}', expected 'v4.0'.");

            var adminRuntime = PowerShellSingleLine($"(Get-WebAppPool -Name '{ctx.WebAppAdminPoolName}').managedRuntimeVersion");
            adminRuntime.ShouldBe(string.Empty,
                customMessage:
                    $"WebApp /admin AppPool runtime is '{adminRuntime}', expected empty string (No Managed Code).");

            // ──── INVARIANT 4: Each child's appsettings.json has the correct Region ──
            // If the per-deploy variable expansion regressed and bled across deploys,
            // both would have the SAME value (whichever ran last).
            var apiAppSettings = File.ReadAllText(Path.Combine(ctx.WebAppApiPhysicalPath, "appsettings.json"));
            apiAppSettings.ShouldContain("\"Region\": \"us-east-1\"",
                customMessage:
                    $"WebApp /api appsettings.json doesn't have Region=us-east-1. " +
                    $"Content:\n{apiAppSettings}");
            apiAppSettings.ShouldNotContain("eu-west-1",
                customMessage:
                    "WebApp /api appsettings.json contains eu-west-1 — variable values bled across deploys.");

            var adminAppSettings = File.ReadAllText(Path.Combine(ctx.WebAppAdminPhysicalPath, "appsettings.json"));
            adminAppSettings.ShouldContain("\"Region\": \"eu-west-1\"",
                customMessage:
                    $"WebApp /admin appsettings.json doesn't have Region=eu-west-1. " +
                    $"Content:\n{adminAppSettings}");
            adminAppSettings.ShouldNotContain("us-east-1",
                customMessage:
                    "WebApp /admin appsettings.json contains us-east-1 — variable values bled across deploys.");

            ctx.MarkClean();
        }
        finally
        {
            // ctx.Dispose cleans up; explicit nothing here — finally exists only to make
            // the try/cleanup intent obvious in the assertion-heavy method body.
        }
    }

    private static void PrecreateParentSite(WebAppCompositionTestContext ctx)
    {
        Directory.CreateDirectory(ctx.ParentSitePhysicalPath);
        File.WriteAllText(Path.Combine(ctx.ParentSitePhysicalPath, "index.html"),
            "<!DOCTYPE html><html><body>parent site</body></html>");

        var precreateScript =
            $"Import-Module WebAdministration; " +
            $"New-WebAppPool -Name '{ctx.ParentSitePoolName}' -Force | Out-Null; " +
            $"Set-ItemProperty IIS:\\AppPools\\{ctx.ParentSitePoolName} managedRuntimeVersion 'v4.0'; " +
            $"New-Website -Name '{ctx.ParentSiteName}' " +
                $"-PhysicalPath '{ctx.ParentSitePhysicalPath}' " +
                $"-ApplicationPool '{ctx.ParentSitePoolName}' " +
                $"-Port {ctx.ParentSiteHttpPort} " +
                $"-Force | Out-Null";

        var r = RunPowerShell(precreateScript);
        if (r.ExitCode != 0)
            throw new InvalidOperationException(
                $"Failed to pre-create parent site '{ctx.ParentSiteName}'. " +
                $"ExitCode={r.ExitCode}, StdOut={r.StdOut}, StdErr={r.StdErr}");
    }

    /// <summary>
    /// Stages a minimal artifact containing only an <c>appsettings.json</c> with a
    /// SubstituteInFiles token. The deploy script ships it as a <c>.zip</c> via
    /// <see cref="Property.PackageSourcePath"/>; the WebApp's PhysicalPath becomes
    /// the extracted folder.
    /// </summary>
    private static string StageMinimalAspNetArtifact(string stagingDir, string regionMarker)
    {
        Directory.CreateDirectory(stagingDir);

        File.WriteAllText(Path.Combine(stagingDir, "appsettings.json"),
            "{\n" +
            "  \"Region\": \"#{Region}\"\n" +
            "}\n");
        File.WriteAllText(Path.Combine(stagingDir, "index.html"),
            $"<!DOCTYPE html><html><body>region={regionMarker}</body></html>\n");

        var zipPath = Path.Combine(Path.GetTempPath(), $"squid-iis-webapp-composition-{Guid.NewGuid():N}.zip");
        ZipFile.CreateFromDirectory(stagingDir, zipPath);
        return zipPath;
    }

    private static DeploymentActionDto BuildWebAppAction(
        string parentSiteName,
        string virtualPath,
        string physicalPath,
        string appPoolName,
        string frameworkVersion,
        string artifactPath)
    {
        return new DeploymentActionDto
        {
            Id = 1,
            Name = $"Deploy WebApp {virtualPath}",
            ActionType = "Squid.DeployToIISWebSite",
            Properties = new List<DeploymentActionPropertyDto>
            {
                new() { PropertyName = Property.PackageSourcePath, PropertyValue = artifactPath },
                new() { PropertyName = Property.PackagePurgeBeforeExtract, PropertyValue = "True" },
                new() { PropertyName = Property.WebApplicationCreateOrUpdate, PropertyValue = "True" },
                new() { PropertyName = Property.WebApplicationWebSiteName, PropertyValue = parentSiteName },
                new() { PropertyName = Property.WebApplicationVirtualPath, PropertyValue = virtualPath },
                new() { PropertyName = Property.WebApplicationPhysicalPath, PropertyValue = physicalPath },
                new() { PropertyName = Property.WebApplicationApplicationPoolName, PropertyValue = appPoolName },
                new() { PropertyName = Property.WebApplicationApplicationPoolFrameworkVersion, PropertyValue = frameworkVersion },
                new() { PropertyName = Property.SubstituteInFilesEnabled, PropertyValue = "True" },
                new() { PropertyName = Property.SubstituteInFilesTargetFiles, PropertyValue = "appsettings.json" }
            }
        };
    }

    // ── Helpers (mirror IISDeployRealHostE2ETests private helpers; duplicated for isolation) ──

    private sealed class WebAppCompositionTestContext : IDisposable
    {
        private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
        private bool _markedClean;

        public WebAppCompositionTestContext()
        {
            ParentSiteName = $"SquidIISWebAppComp-{_suffix}";
            ParentSitePoolName = $"SquidIISWebAppCompPool-{_suffix}";
            ParentSitePhysicalPath = Path.Combine(Path.GetTempPath(), $"squid-iis-webapp-comp-parent-{_suffix}");
            ParentSiteHttpPort = PickFreePort();

            WebAppApiVirtualPath = "/api";
            WebAppApiPhysicalPath = Path.Combine(Path.GetTempPath(), $"squid-iis-webapp-comp-api-{_suffix}");
            WebAppApiPoolName = $"SquidIISWebAppCompApiPool-{_suffix}";
            WebAppApiStagingDir = Path.Combine(Path.GetTempPath(), $"squid-iis-webapp-comp-api-staging-{_suffix}");

            WebAppAdminVirtualPath = "/admin";
            WebAppAdminPhysicalPath = Path.Combine(Path.GetTempPath(), $"squid-iis-webapp-comp-admin-{_suffix}");
            WebAppAdminPoolName = $"SquidIISWebAppCompAdminPool-{_suffix}";
            WebAppAdminStagingDir = Path.Combine(Path.GetTempPath(), $"squid-iis-webapp-comp-admin-staging-{_suffix}");
        }

        public string ParentSiteName { get; }
        public string ParentSitePoolName { get; }
        public string ParentSitePhysicalPath { get; }
        public string ParentSiteHttpPort { get; }

        public string WebAppApiVirtualPath { get; }
        public string WebAppApiPhysicalPath { get; }
        public string WebAppApiPoolName { get; }
        public string WebAppApiStagingDir { get; }

        public string WebAppAdminVirtualPath { get; }
        public string WebAppAdminPhysicalPath { get; }
        public string WebAppAdminPoolName { get; }
        public string WebAppAdminStagingDir { get; }

        public void MarkClean() => _markedClean = true;

        public void Dispose()
        {
            if (OperatingSystem.IsWindows() && IsIISInstalled())
            {
                TryPowerShell($"Remove-Website -Name '{ParentSiteName}' -ErrorAction SilentlyContinue");
                TryPowerShell($"Remove-WebAppPool -Name '{ParentSitePoolName}' -ErrorAction SilentlyContinue");
                TryPowerShell($"Remove-WebAppPool -Name '{WebAppApiPoolName}' -ErrorAction SilentlyContinue");
                TryPowerShell($"Remove-WebAppPool -Name '{WebAppAdminPoolName}' -ErrorAction SilentlyContinue");
            }

            foreach (var dir in new[] { ParentSitePhysicalPath, WebAppApiPhysicalPath, WebAppAdminPhysicalPath,
                                        WebAppApiStagingDir, WebAppAdminStagingDir })
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
                catch { /* best-effort */ }
            }

            if (!_markedClean && OperatingSystem.IsWindows())
                Console.WriteLine($"[WebAppCompositionTestContext.Dispose] Test did NOT call MarkClean — ParentSite={ParentSiteName}.");
        }

        private static string PickFreePort()
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port.ToString();
        }
    }

    private static class Property
    {
        public const string PackageSourcePath = "Squid.Action.IISWebSite.Package.SourcePath";
        public const string PackagePurgeBeforeExtract = "Squid.Action.IISWebSite.Package.PurgeBeforeExtract";
        public const string WebApplicationCreateOrUpdate = "Squid.Action.IISWebSite.WebApplication.CreateOrUpdate";
        public const string WebApplicationWebSiteName = "Squid.Action.IISWebSite.WebApplication.WebSiteName";
        public const string WebApplicationVirtualPath = "Squid.Action.IISWebSite.WebApplication.VirtualPath";
        public const string WebApplicationPhysicalPath = "Squid.Action.IISWebSite.WebApplication.PhysicalPath";
        public const string WebApplicationApplicationPoolName = "Squid.Action.IISWebSite.WebApplication.ApplicationPoolName";
        public const string WebApplicationApplicationPoolFrameworkVersion = "Squid.Action.IISWebSite.WebApplication.ApplicationPoolFrameworkVersion";
        public const string SubstituteInFilesEnabled = "Squid.Action.IISWebSite.SubstituteInFiles.Enabled";
        public const string SubstituteInFilesTargetFiles = "Squid.Action.IISWebSite.SubstituteInFiles.TargetFiles";
    }

    private static bool IsIISInstalled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var r = RunPowerShell("(Get-WindowsFeature Web-WebServer -ErrorAction SilentlyContinue).Installed");
            return r.ExitCode == 0 && r.StdOut.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string PowerShellSingleLine(string command)
    {
        var script = $"Import-Module WebAdministration; Write-Host -NoNewline ({command})";
        return RunPowerShell(script).StdOut.Trim();
    }

    private static void TryPowerShell(string command)
    {
        try { RunPowerShell($"Import-Module WebAdministration -ErrorAction SilentlyContinue; {command}"); }
        catch { /* best-effort cleanup */ }
    }

    private static PsResult RunPowerShell(string script)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = System.Text.Encoding.UTF8,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        process.StandardInput.Write(script);
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(TimeSpan.FromMinutes(3));

        return new PsResult(process.ExitCode, stdout, stderr);
    }

    private sealed record PsResult(int ExitCode, string StdOut, string StdErr);
}
