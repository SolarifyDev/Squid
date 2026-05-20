using System.IO.Compression;
using Squid.Core.Services.DeploymentExecution.Tentacle.Handlers;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// Real-world composite simulating a multi-host parallel rollout, on a single CI
/// runner. Production deploys commonly land the SAME package on 5-20 IIS hosts
/// concurrently — a rolling update across a web farm. CI doesn't have 20
/// Windows runners, so we approximate the contract by running TWO independent
/// deploys against the same host (different site names + ports + physical
/// paths) CONCURRENTLY via <c>Task.Run</c>. The invariant under test: per-site
/// journal isolation, no cross-site state leakage, both deploys complete.
///
/// <para><b>Production gap closed</b>: every existing IIS test runs ONE deploy at
/// a time. None exercises the concurrency contract that the deploy script
/// implicitly relies on (per-site journal file at
/// <c>%ProgramData%\Squid\IISDeploy\journal\&lt;site&gt;.json</c>, per-site IIS
/// metabase entries, per-site mutex naming). A regression that shared journal
/// state across sites (e.g. a single global journal file) would only surface
/// here.</para>
///
/// <para><b>Caveat — not a true multi-host test</b>: real multi-host parallel
/// rollouts exercise distinct IIS metabase databases on distinct OS hosts. This
/// test runs against ONE host's SCM, so it doesn't catch cross-host failure
/// modes (network partitions, OS-version drift, registry differences). True
/// multi-host coverage requires a 2-runner CI matrix — logged as Phase 3
/// backlog. What this DOES test on one host: per-site journal isolation,
/// concurrent IIS configure operations against the metabase, port-allocation
/// non-collision, two different site-name registrations succeeding.</para>
///
/// <para><b>Tier</b>: 🟢 High-fidelity (for the single-host concurrency
/// invariant). Real IIS metabase, real PowerShell, real journal files written
/// to <c>%ProgramData%</c>.</para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.IISDeploy)]
public sealed class IISDeployRealWorldMultiHostE2ETests
{
    [Fact]
    public async Task TwoConcurrentDeploys_DifferentSites_BothComplete_JournalsAreSiteScoped()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new MultiHostTestContext();
        ctx.RegisterDeploymentJournalForCleanup(ctx.SiteAName);
        ctx.RegisterDeploymentJournalForCleanup(ctx.SiteBName);

        // ──── STAGE 1: Stage two distinct artifacts ──────────────────────────────
        var artifactA = StageArtifact(ctx.SiteAStagingDir, "site-a-content");
        var artifactB = StageArtifact(ctx.SiteBStagingDir, "site-b-content");

        var variablesA = new List<VariableDto> { new() { Name = "AppName", Value = "ServiceA" } };
        var variablesB = new List<VariableDto> { new() { Name = "AppName", Value = "ServiceB" } };

        var actionA = BuildSiteAction(ctx.SiteAName, ctx.SiteAPoolName, ctx.SiteAPhysicalPath, ctx.SiteAHttpPort, artifactA);
        var actionB = BuildSiteAction(ctx.SiteBName, ctx.SiteBPoolName, ctx.SiteBPhysicalPath, ctx.SiteBHttpPort, artifactB);

        var scriptA = IISDeployScriptBuilder.Build(actionA, variablesA);
        var scriptB = IISDeployScriptBuilder.Build(actionB, variablesB);

        // ──── STAGE 2: Dispatch both deploys CONCURRENTLY ────────────────────────
        //
        // Task.Run forks two PowerShell processes that run simultaneously against
        // the same OS host's SCM + IIS metabase + %ProgramData% journal directory.
        // If any of those subsystems' production code assumed exclusive access,
        // ONE deploy would fail or corrupt the other.
        var taskA = Task.Run(() => RunPowerShell(scriptA));
        var taskB = Task.Run(() => RunPowerShell(scriptB));

        await Task.WhenAll(taskA, taskB).ConfigureAwait(false);

        var resultA = await taskA.ConfigureAwait(false);
        var resultB = await taskB.ConfigureAwait(false);

        // ──── INVARIANT 1: Both deploys exited 0 ─────────────────────────────────
        resultA.ExitCode.ShouldBe(0,
            customMessage:
                $"Site A deploy failed under concurrent load.\nSTDOUT:\n{Truncate(resultA.StdOut, 3000)}" +
                $"\n\nSTDERR:\n{Truncate(resultA.StdErr, 3000)}");

        resultB.ExitCode.ShouldBe(0,
            customMessage:
                $"Site B deploy failed under concurrent load.\nSTDOUT:\n{Truncate(resultB.StdOut, 3000)}" +
                $"\n\nSTDERR:\n{Truncate(resultB.StdErr, 3000)}");

        // ──── INVARIANT 2: Both sites exist in the metabase ──────────────────────
        var siteAExists = PowerShellSingleLine(
            $"if (Get-Website -Name '{ctx.SiteAName}' -ErrorAction SilentlyContinue) {{ 'true' }} else {{ 'false' }}");
        siteAExists.ShouldBe("true", customMessage: $"Site A '{ctx.SiteAName}' missing.");

        var siteBExists = PowerShellSingleLine(
            $"if (Get-Website -Name '{ctx.SiteBName}' -ErrorAction SilentlyContinue) {{ 'true' }} else {{ 'false' }}");
        siteBExists.ShouldBe("true", customMessage: $"Site B '{ctx.SiteBName}' missing.");

        // ──── INVARIANT 3: Per-site journals exist + record correct Success ─────
        //
        // If journals shared a single global file, the slower deploy would
        // overwrite the faster one and one site's entry would be missing OR mixed.
        var journalDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Squid", "IISDeploy", "journal");

        var journalA = Path.Combine(journalDir, $"{ctx.SiteAName}.json");
        File.Exists(journalA).ShouldBeTrue(
            customMessage: $"Site A journal '{journalA}' missing — per-site journal isolation broken.");

        var journalAContent = File.ReadAllText(journalA);
        journalAContent.ShouldContain("\"Status\": \"Success\"",
            customMessage: $"Site A journal Status != Success. Content:\n{journalAContent}");
        journalAContent.ShouldContain(ctx.SiteAName,
            customMessage: $"Site A journal references the wrong site. Content:\n{journalAContent}");

        var journalB = Path.Combine(journalDir, $"{ctx.SiteBName}.json");
        File.Exists(journalB).ShouldBeTrue(
            customMessage: $"Site B journal '{journalB}' missing — per-site journal isolation broken.");

        var journalBContent = File.ReadAllText(journalB);
        journalBContent.ShouldContain("\"Status\": \"Success\"",
            customMessage: $"Site B journal Status != Success. Content:\n{journalBContent}");
        journalBContent.ShouldContain(ctx.SiteBName,
            customMessage: $"Site B journal references the wrong site. Content:\n{journalBContent}");

        // ──── INVARIANT 4: Each site's WebRoot has its own content ───────────────
        // If extraction collided, the two sites' WebRoots would point at the same
        // dir and the second extract would clobber the first.
        var siteAContent = File.ReadAllText(Path.Combine(ctx.SiteAPhysicalPath, "marker.txt"));
        siteAContent.Trim().ShouldBe("site-a-content",
            customMessage: $"Site A marker shows wrong content '{siteAContent}' — extraction collision.");

        var siteBContent = File.ReadAllText(Path.Combine(ctx.SiteBPhysicalPath, "marker.txt"));
        siteBContent.Trim().ShouldBe("site-b-content",
            customMessage: $"Site B marker shows wrong content '{siteBContent}' — extraction collision.");

        // ──── INVARIANT 5: Variable substitution stayed per-deploy ──────────────
        // Same #{AppName} token in both deploys, different variable values.
        // If the rewriter's variable scope leaked between processes, both sites
        // would end up with the same AppName.
        var appSettingsA = File.ReadAllText(Path.Combine(ctx.SiteAPhysicalPath, "appsettings.json"));
        appSettingsA.ShouldContain("\"AppName\": \"ServiceA\"",
            customMessage:
                $"Site A appsettings.json doesn't have AppName=ServiceA. " +
                $"Content:\n{appSettingsA}");
        appSettingsA.ShouldNotContain("ServiceB",
            customMessage: "Site A appsettings.json has ServiceB — cross-deploy variable bleed.");

        var appSettingsB = File.ReadAllText(Path.Combine(ctx.SiteBPhysicalPath, "appsettings.json"));
        appSettingsB.ShouldContain("\"AppName\": \"ServiceB\"",
            customMessage:
                $"Site B appsettings.json doesn't have AppName=ServiceB. " +
                $"Content:\n{appSettingsB}");
        appSettingsB.ShouldNotContain("ServiceA",
            customMessage: "Site B appsettings.json has ServiceA — cross-deploy variable bleed.");

        ctx.MarkClean();
    }

    private static string StageArtifact(string stagingDir, string contentMarker)
    {
        Directory.CreateDirectory(stagingDir);

        File.WriteAllText(Path.Combine(stagingDir, "appsettings.json"),
            "{\n" +
            "  \"AppName\": \"#{AppName}\"\n" +
            "}\n");
        File.WriteAllText(Path.Combine(stagingDir, "marker.txt"), contentMarker);
        File.WriteAllText(Path.Combine(stagingDir, "index.html"),
            $"<!DOCTYPE html><html><body>{contentMarker}</body></html>");

        var zipPath = Path.Combine(Path.GetTempPath(), $"squid-iis-multihost-{Guid.NewGuid():N}.zip");
        ZipFile.CreateFromDirectory(stagingDir, zipPath);
        return zipPath;
    }

    private static DeploymentActionDto BuildSiteAction(
        string siteName,
        string poolName,
        string physicalPath,
        string httpPort,
        string artifactPath)
    {
        return new DeploymentActionDto
        {
            Id = 1,
            Name = $"Deploy {siteName}",
            ActionType = "Squid.DeployToIISWebSite",
            Properties = new List<DeploymentActionPropertyDto>
            {
                new() { PropertyName = Property.PackageSourcePath, PropertyValue = artifactPath },
                new() { PropertyName = Property.PackagePurgeBeforeExtract, PropertyValue = "True" },
                new() { PropertyName = Property.CreateOrUpdateWebSite, PropertyValue = "True" },
                new() { PropertyName = Property.WebSiteName, PropertyValue = siteName },
                new() { PropertyName = Property.ApplicationPoolName, PropertyValue = poolName },
                new() { PropertyName = Property.ApplicationPoolIdentityType, PropertyValue = "ApplicationPoolIdentity" },
                new() { PropertyName = Property.ApplicationPoolFrameworkVersion, PropertyValue = "v4.0" },
                new() { PropertyName = Property.WebRoot, PropertyValue = physicalPath },
                new() { PropertyName = Property.Bindings,
                    PropertyValue = $"[{{\"protocol\":\"http\",\"port\":\"{httpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true,\"requireSni\":false}}]" },
                new() { PropertyName = Property.EnableAnonymousAuthentication, PropertyValue = "True" },
                new() { PropertyName = Property.EnableBasicAuthentication, PropertyValue = "False" },
                new() { PropertyName = Property.EnableWindowsAuthentication, PropertyValue = "False" },
                new() { PropertyName = Property.StartApplicationPool, PropertyValue = "True" },
                new() { PropertyName = Property.StartWebSite, PropertyValue = "True" },
                new() { PropertyName = Property.SubstituteInFilesEnabled, PropertyValue = "True" },
                new() { PropertyName = Property.SubstituteInFilesTargetFiles, PropertyValue = "appsettings.json" },
                new() { PropertyName = Property.PackageSkipIfAlreadyInstalled, PropertyValue = "True" }
            }
        };
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + $"... [truncated, total {s.Length} chars]";

    // ── Helpers (mirror IISDeployRealHostE2ETests; duplicated for isolation) ──

    private sealed class MultiHostTestContext : IDisposable
    {
        private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
        private readonly List<string> _journalFilesToClean = new();
        private bool _markedClean;

        public MultiHostTestContext()
        {
            SiteAName = $"SquidIISMultiHostA-{_suffix}";
            SiteAPoolName = $"SquidIISMultiHostAPool-{_suffix}";
            SiteAPhysicalPath = Path.Combine(Path.GetTempPath(), $"squid-iis-multihost-a-{_suffix}");
            SiteAStagingDir = Path.Combine(Path.GetTempPath(), $"squid-iis-multihost-a-staging-{_suffix}");
            SiteAHttpPort = PickFreePort();

            SiteBName = $"SquidIISMultiHostB-{_suffix}";
            SiteBPoolName = $"SquidIISMultiHostBPool-{_suffix}";
            SiteBPhysicalPath = Path.Combine(Path.GetTempPath(), $"squid-iis-multihost-b-{_suffix}");
            SiteBStagingDir = Path.Combine(Path.GetTempPath(), $"squid-iis-multihost-b-staging-{_suffix}");
            SiteBHttpPort = PickFreePort();
        }

        public string SiteAName { get; }
        public string SiteAPoolName { get; }
        public string SiteAPhysicalPath { get; }
        public string SiteAStagingDir { get; }
        public string SiteAHttpPort { get; }

        public string SiteBName { get; }
        public string SiteBPoolName { get; }
        public string SiteBPhysicalPath { get; }
        public string SiteBStagingDir { get; }
        public string SiteBHttpPort { get; }

        public void RegisterDeploymentJournalForCleanup(string siteName)
        {
            var programData = Environment.GetEnvironmentVariable("ProgramData");
            if (string.IsNullOrEmpty(programData)) return;
            var safeName = System.Text.RegularExpressions.Regex.Replace(siteName, @"[^A-Za-z0-9._-]", "_");
            var journalPath = Path.Combine(programData, "Squid", "IISDeploy", "journal", $"{safeName}.json");
            _journalFilesToClean.Add(journalPath);
        }

        public void MarkClean() => _markedClean = true;

        public void Dispose()
        {
            if (OperatingSystem.IsWindows() && IsIISInstalled())
            {
                TryPowerShell($"Remove-Website -Name '{SiteAName}' -ErrorAction SilentlyContinue");
                TryPowerShell($"Remove-Website -Name '{SiteBName}' -ErrorAction SilentlyContinue");
                TryPowerShell($"Remove-WebAppPool -Name '{SiteAPoolName}' -ErrorAction SilentlyContinue");
                TryPowerShell($"Remove-WebAppPool -Name '{SiteBPoolName}' -ErrorAction SilentlyContinue");
            }

            foreach (var journal in _journalFilesToClean)
            {
                try { if (File.Exists(journal)) File.Delete(journal); }
                catch { /* best-effort */ }
            }

            foreach (var dir in new[] { SiteAPhysicalPath, SiteBPhysicalPath, SiteAStagingDir, SiteBStagingDir })
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
                catch { /* best-effort */ }
            }

            if (!_markedClean && OperatingSystem.IsWindows())
                Console.WriteLine($"[MultiHostTestContext.Dispose] Test did NOT call MarkClean — SiteA={SiteAName}, SiteB={SiteBName}.");
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
        public const string PackageSkipIfAlreadyInstalled = "Squid.Action.IISWebSite.Package.SkipIfAlreadyInstalled";
        public const string CreateOrUpdateWebSite = "Squid.Action.IISWebSite.CreateOrUpdateWebSite";
        public const string WebSiteName = "Squid.Action.IISWebSite.WebSiteName";
        public const string ApplicationPoolName = "Squid.Action.IISWebSite.ApplicationPoolName";
        public const string ApplicationPoolIdentityType = "Squid.Action.IISWebSite.ApplicationPoolIdentityType";
        public const string ApplicationPoolFrameworkVersion = "Squid.Action.IISWebSite.ApplicationPoolFrameworkVersion";
        public const string WebRoot = "Squid.Action.IISWebSite.WebRoot";
        public const string Bindings = "Squid.Action.IISWebSite.Bindings";
        public const string EnableAnonymousAuthentication = "Squid.Action.IISWebSite.EnableAnonymousAuthentication";
        public const string EnableBasicAuthentication = "Squid.Action.IISWebSite.EnableBasicAuthentication";
        public const string EnableWindowsAuthentication = "Squid.Action.IISWebSite.EnableWindowsAuthentication";
        public const string StartApplicationPool = "Squid.Action.IISWebSite.StartApplicationPool";
        public const string StartWebSite = "Squid.Action.IISWebSite.StartWebSite";
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
        catch { /* best-effort */ }
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
        // Two concurrent deploys can each take 30-60s including IIS init,
        // so we give 5 min total.
        process.WaitForExit(TimeSpan.FromMinutes(5));

        return new PsResult(process.ExitCode, stdout, stderr);
    }

    private sealed record PsResult(int ExitCode, string StdOut, string StdErr);
}
