using System.IO.Compression;
using Squid.Core.Services.DeploymentExecution.Tentacle.Handlers;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// Composite end-to-end test: stages a realistic <c>.NET</c> application artifact and
/// exercises EVERY operator-facing feature of <c>Squid.DeployToIISWebSite</c> in a single
/// deploy action. Mirrors the canonical Octopus operator workflow:
///
/// <list type="number">
///   <item><b>Package extraction</b> — operator-staged <c>app.zip</c> extracted to <c>WebRoot</c>
///         (with purge of prior files)</item>
///   <item><b>PreDeploy custom script</b> — writes a sentinel file proving the hook fired</item>
///   <item><b>SubstituteInFiles</b> — replaces <c>#{X}</c> tokens in <c>appsettings.json</c>
///         (text-level substitution)</item>
///   <item><b>ConfigurationTransforms (XDT)</b> — applies <c>web.Release.config</c> overlay
///         to <c>web.config</c> via <c>Microsoft.Web.XmlTransform</c></item>
///   <item><b>ConfigurationVariables</b> — replaces <c>&lt;appSettings/add@key&gt;</c> and
///         <c>&lt;connectionStrings/add@name&gt;</c> in <c>web.config</c></item>
///   <item><b>StructuredConfigurationVariables (JSON)</b> — replaces nested leaf values
///         in <c>appsettings.json</c> using both <c>:</c> and <c>.</c> path separators</item>
///   <item><b>IIS configure</b> — creates the site / app pool / bindings + applies all three
///         auth toggles</item>
///   <item><b>PostDeploy custom script</b> — writes a sentinel containing the live site
///         state via <c>Get-Website</c></item>
/// </list>
///
/// <para>The test asserts the END STATE — every file's content, IIS metabase entries, sentinel
/// contents — proving the eight phases compose correctly into a single coherent
/// operator-facing deploy. This is the canonical "does the action work end-to-end on a real
/// .NET artifact" question.</para>
///
/// <para><b>Tier</b>: 🟢 High-fidelity. Real IIS, real PowerShell, real
/// <c>Microsoft.Web.XmlTransform.dll</c>, real <c>appsettings.json</c> + <c>web.config</c>
/// being parsed and rewritten by production paths. Skip-on-non-Windows guard + IIS-feature
/// probe keep cross-OS dev hosts running a clean "0 / 0" instead of spurious failures.</para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.IISDeploy)]
public sealed class IISDeployRealWorldDotNetAppE2ETests
{
    [Fact]
    public void RealWorld_FullDotNetAppDeploy_AllEightFeaturesComposeCorrectly()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;
        if (!IsMicrosoftWebXmlTransformAvailable()) return;

        using var ctx = new IISTestContext();

        // ──── STAGE 1: Build the operator-facing .NET app artifact ─────────────────
        //
        // Constructs an in-memory representation of a realistic ASP.NET / .NET Core
        // deployment package containing:
        //   - appsettings.json (with tokens for SubstituteInFiles + nested keys for StructuredConfigurationVariables)
        //   - web.config (with appSettings + connectionStrings for ConfigurationVariables)
        //   - web.Release.config (XDT transforms)
        //   - index.html (proves recursive extraction works)
        //   - bin/ subdir with a fake DLL (proves nested-directory extraction)
        var artifactPath = StageNetAppArtifact(ctx);

        // Pre-stage a stale marker in WebRoot — Package.PurgeBeforeExtract must wipe it
        // before extraction. Proves the purge contract end-to-end.
        var stalePath = Path.Combine(ctx.PhysicalPath, "stale-from-previous-deploy.txt");
        File.WriteAllText(stalePath, "should be purged");

        // Pre/PostDeploy hook witnesses.
        var preDeploySentinel = ctx.RegisterSentinelPath("realworld-predeploy");
        var postDeploySentinel = ctx.RegisterSentinelPath("realworld-postdeploy");

        // ──── STAGE 2: Define the operator's Squid variable set ───────────────────
        //
        // Variables fall into three groups, each consumed by a different rewriter:
        //
        //   Group A — SubstituteInFiles (#{X} text tokens in appsettings.json):
        //     - AppVersion        → "1.4.2"      (replaces `#{AppVersion}`)
        //     - EnvironmentTag    → "Production" (replaces `#{EnvironmentTag}`)
        //
        //   Group B — ConfigurationVariables (XML <add key=...>/<add name=...> in web.config):
        //     - ApiUrl            → URL          (replaces appSetting `value` for key="ApiUrl")
        //     - OrdersDb          → connstr      (replaces connectionString for name="OrdersDb")
        //
        //   Group C — StructuredConfigurationVariables (JSON nested leaves in appsettings.json):
        //     - Logging:LogLevel:Default → "Debug"      (colon path — .NET Core style)
        //     - ConnectionStrings.CacheDb → "...cache"  (dot path — .NET Framework style)
        var variables = new List<VariableDto>
        {
            // Group A
            new() { Name = "AppVersion", Value = "1.4.2" },
            new() { Name = "EnvironmentTag", Value = "Production" },
            // Group B
            new() { Name = "ApiUrl", Value = "https://api.prod.example.com/v2" },
            new() { Name = "OrdersDb", Value = "Server=prod-db-cluster;Database=Orders;Integrated Security=True" },
            // Group C — note the variable NAMES use both separators to prove both are accepted
            new() { Name = "Logging:LogLevel:Default", Value = "Debug" },
            new() { Name = "ConnectionStrings.CacheDb", Value = "Redis=cache-prod-1.local:6379" }
        };

        // ──── STAGE 3: Build the action with EVERY feature toggle ON ──────────────
        //
        // This is the realistic "operator-ticked-every-checkbox" configuration that
        // mirrors how Octopus operators set up an IIS deploy step with the full
        // pipeline enabled.
        var action = BuildAction(
            // — Package extraction —
            (Property.PackageSourcePath, artifactPath),
            (Property.PackagePurgeBeforeExtract, "True"),
            // — IIS configure (Phase 1+3+3.5+4) —
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings,
                $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}}]"),
            (Property.EnableAnonymousAuthentication, "True"),
            (Property.EnableBasicAuthentication, "False"),
            (Property.EnableWindowsAuthentication, "True"),
            (Property.StartApplicationPool, "True"),
            (Property.StartWebSite, "True"),
            // — Custom scripts (Phase 5) —
            (Property.CustomScriptsPreDeploy,
                $"Set-Content -Path '{preDeploySentinel}' -Value 'predeploy-fired-at-' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"),
            (Property.CustomScriptsPostDeploy,
                $"$site = Get-Website -Name '{ctx.SiteName}'; " +
                $"Set-Content -Path '{postDeploySentinel}' -Value \"postdeploy-saw-site-state=$($site.State)\""),
            // — SubstituteInFiles (Phase 8) —
            (Property.SubstituteInFilesEnabled, "True"),
            (Property.SubstituteInFilesTargetFiles, "appsettings.json"),
            // — ConfigurationTransforms / XDT (Phase 7) —
            (Property.ConfigurationTransformsEnabled, "True"),
            (Property.ConfigurationTransformsEnvironmentName, "Production"),
            // — ConfigurationVariables (Phase 6) —
            (Property.ConfigurationVariablesEnabled, "True"),
            // — StructuredConfigurationVariables / JSON (Phase 9) —
            (Property.StructuredConfigurationVariablesEnabled, "True"),
            (Property.StructuredConfigurationVariablesTargets, "appsettings.json"));

        // ──── STAGE 4: Build + run the deploy script ──────────────────────────────
        var script = IISDeployScriptBuilder.Build(action, variables);
        var result = RunPowerShell(script);

        result.ExitCode.ShouldBe(0,
            customMessage:
                "Full real-world deploy failed — at least one of the 8 features broke. " +
                "Triage steps:\n" +
                $"  1. Inspect STDOUT for which phase wrote the last Write-Host line\n" +
                $"  2. Check sentinel files: predeploy '{preDeploySentinel}', postdeploy '{postDeploySentinel}'\n" +
                $"  3. `Get-Website -Name '{ctx.SiteName}'` to check IIS metabase\n" +
                $"  4. `Get-ChildItem '{ctx.PhysicalPath}' -Recurse` to inspect WebRoot contents\n\n" +
                $"STDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        // ──── STAGE 5: Verify Package Extraction ──────────────────────────────────
        File.Exists(stalePath).ShouldBeFalse(
            customMessage: $"Stale file '{stalePath}' from prior deploy survived PurgeBeforeExtract — purge contract broken.");

        File.Exists(Path.Combine(ctx.PhysicalPath, "index.html")).ShouldBeTrue(
            customMessage: $"index.html missing from WebRoot — Expand-IISPackage didn't extract into '{ctx.PhysicalPath}'.");

        File.Exists(Path.Combine(ctx.PhysicalPath, "bin", "OrderApi.dll")).ShouldBeTrue(
            customMessage: "Nested bin/OrderApi.dll missing — recursive extraction broken.");

        // ──── STAGE 6: Verify Custom PreDeploy Script Fired ───────────────────────
        File.Exists(preDeploySentinel).ShouldBeTrue(
            customMessage: $"PreDeploy sentinel '{preDeploySentinel}' missing — custom PreDeploy script didn't run.");

        File.ReadAllText(preDeploySentinel).ShouldStartWith("predeploy-fired-at-",
            customMessage: "PreDeploy sentinel content doesn't match expected format — script body may have been mangled.");

        // ──── STAGE 7: Verify SubstituteInFiles (#{X} tokens) ─────────────────────
        var renderedAppSettings = File.ReadAllText(Path.Combine(ctx.PhysicalPath, "appsettings.json"));

        renderedAppSettings.ShouldContain("\"OrderApi v1.4.2\"",
            customMessage: $"#{{AppVersion}} token NOT replaced. appsettings.json:\n{renderedAppSettings}");
        renderedAppSettings.ShouldContain("\"Production\"",
            customMessage: "#{EnvironmentTag} token NOT replaced.");
        renderedAppSettings.ShouldNotContain("#{AppVersion}");
        renderedAppSettings.ShouldNotContain("#{EnvironmentTag}");

        // ──── STAGE 8: Verify StructuredConfigurationVariables (JSON nested) ──────
        // Logging:LogLevel:Default — colon path, .NET Core style
        renderedAppSettings.ShouldContain("\"Debug\"",
            customMessage: $"Logging:LogLevel:Default nested leaf NOT replaced via colon path. appsettings.json:\n{renderedAppSettings}");
        renderedAppSettings.ShouldNotContain("\"Information\"",
            customMessage: "Original Logging:LogLevel:Default value still present — replacement didn't persist.");

        // ConnectionStrings.CacheDb — dot path, .NET Framework style
        renderedAppSettings.ShouldContain("Redis=cache-prod-1.local:6379",
            customMessage: "ConnectionStrings.CacheDb nested leaf NOT replaced via dot path. " +
                          "Confirms dot-separator path matching is working.");
        renderedAppSettings.ShouldNotContain("Server=cache-localhost");

        // ──── STAGE 9: Verify ConfigurationTransforms (XDT) ───────────────────────
        var renderedWebConfig = File.ReadAllText(Path.Combine(ctx.PhysicalPath, "web.config"));

        // web.Release.config XDT transform replaces MaxRetries value (3 → 10)
        renderedWebConfig.ShouldContain("value=\"10\"",
            customMessage:
                $"XDT (web.Release.config) did NOT apply. MaxRetries should be 10 after transform. web.config:\n{renderedWebConfig}");
        renderedWebConfig.ShouldNotContain("value=\"3\"",
            customMessage: "MaxRetries=3 still present — XDT SetAttributes didn't run.");

        // ──── STAGE 10: Verify ConfigurationVariables (appSettings + connStrings) ──
        // ApiUrl matches a Squid variable name — its appSetting value gets replaced
        renderedWebConfig.ShouldContain("value=\"https://api.prod.example.com/v2\"",
            customMessage: $"ApiUrl variable didn't replace appSettings/add[@key='ApiUrl'] value. web.config:\n{renderedWebConfig}");
        renderedWebConfig.ShouldNotContain("https://localhost/api",
            customMessage: "Original ApiUrl value still present.");

        // OrdersDb matches a Squid variable name — its connectionString gets replaced
        renderedWebConfig.ShouldContain("Server=prod-db-cluster",
            customMessage: "OrdersDb connectionString didn't get replaced by ConfigurationVariables.");
        renderedWebConfig.ShouldNotContain("Server=local-db");

        // ──── STAGE 11: Verify IIS Configure (the leaf) ───────────────────────────
        var siteExists = PowerShellSingleLine(
            $"if (Get-Website -Name '{ctx.SiteName}' -ErrorAction SilentlyContinue) {{ 'true' }} else {{ 'false' }}");
        siteExists.ShouldBe("true",
            customMessage: $"IIS site '{ctx.SiteName}' not created — the IIS configure leaf failed.");

        var poolState = PowerShellSingleLine($"(Get-WebAppPool -Name '{ctx.PoolName}').State");
        poolState.ShouldBe("Started",
            customMessage: $"App pool '{ctx.PoolName}' state is '{poolState}' — expected 'Started' (StartApplicationPool=True).");

        // Auth flags — anonymous + windows ON, basic OFF
        ReadAuthEnabledFlag(ctx.SiteName, "anonymousAuthentication").ToLowerInvariant().ShouldBe("true",
            customMessage: "Anonymous authentication not enabled — appcmd path broken.");
        ReadAuthEnabledFlag(ctx.SiteName, "basicAuthentication").ToLowerInvariant().ShouldBe("false");
        ReadAuthEnabledFlag(ctx.SiteName, "windowsAuthentication").ToLowerInvariant().ShouldBe("true");

        // Binding present on the configured port
        var bindingInfo = RunPowerShell(
            $"Import-Module WebAdministration; " +
            $"(Get-WebBinding -Name '{ctx.SiteName}' | ForEach-Object {{ $_.bindingInformation }}) -join ';'").StdOut.Trim();
        bindingInfo.ShouldContain($":{ctx.HttpPort}:",
            customMessage: $"HTTP binding on port {ctx.HttpPort} missing — Bindings JSON didn't apply.");

        // ──── STAGE 12: Verify Custom PostDeploy Script Fired AFTER IIS Configure ──
        // PostDeploy is the canary that proves ordering: it ran AFTER the IIS configure
        // because it queried Get-Website and saw the site in "Started" state.
        File.Exists(postDeploySentinel).ShouldBeTrue(
            customMessage: $"PostDeploy sentinel '{postDeploySentinel}' missing — PostDeploy didn't fire.");

        var postDeployContent = File.ReadAllText(postDeploySentinel).Trim();
        postDeployContent.ShouldBe("postdeploy-saw-site-state=Started",
            customMessage:
                $"PostDeploy observed site state '{postDeployContent}' — expected 'postdeploy-saw-site-state=Started'. " +
                "If the value is 'Stopped' or empty, PostDeploy ran BEFORE the IIS configure (ordering broken). " +
                "If sentinel content is malformed, the script body got mangled (escape issue).");

        ctx.MarkClean();
    }

    /// <summary>
    /// Stages a realistic <c>.NET</c> application package on disk and returns the path to the
    /// resulting <c>.zip</c> archive. The package contents are designed to exercise every
    /// rewriter in the IIS deploy pipeline:
    ///
    /// <list type="bullet">
    ///   <item><c>appsettings.json</c> — contains <c>#{AppVersion}</c> tokens (consumed by
    ///         SubstituteInFiles) AND nested values like <c>Logging.LogLevel.Default</c>
    ///         (consumed by StructuredConfigurationVariables)</item>
    ///   <item><c>web.config</c> — contains <c>&lt;appSettings/add@key&gt;</c> entries for
    ///         ApiUrl (matched by ConfigurationVariables) and MaxRetries (transformed by XDT);
    ///         plus a <c>&lt;connectionStrings/add@name&gt;</c> for OrdersDb</item>
    ///   <item><c>web.Release.config</c> — XDT overlay that changes MaxRetries to 10</item>
    ///   <item><c>index.html</c> — proves recursive extraction</item>
    ///   <item><c>bin/OrderApi.dll</c> — proves nested-directory extraction (empty file)</item>
    /// </list>
    /// </summary>
    private static string StageNetAppArtifact(IISTestContext ctx)
    {
        var stagingDir = Path.Combine(Path.GetTempPath(), $"squid-iis-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);
        Directory.CreateDirectory(Path.Combine(stagingDir, "bin"));
        ctx.RegisterTempDirForCleanup(stagingDir);

        // appsettings.json — designed for BOTH SubstituteInFiles (text tokens at top)
        // AND StructuredConfigurationVariables (nested JSON values at bottom).
        File.WriteAllText(Path.Combine(stagingDir, "appsettings.json"),
            "{\n" +
            "  \"ApplicationName\": \"OrderApi v#{AppVersion}\",\n" +
            "  \"Environment\": \"#{EnvironmentTag}\",\n" +
            "  \"Logging\": {\n" +
            "    \"LogLevel\": {\n" +
            "      \"Default\": \"Information\"\n" +
            "    }\n" +
            "  },\n" +
            "  \"ConnectionStrings\": {\n" +
            "    \"CacheDb\": \"Server=cache-localhost\"\n" +
            "  }\n" +
            "}\n");

        // web.config — for ConfigurationVariables (appSettings + connectionStrings).
        // MaxRetries=3 here will get OVERRIDDEN by web.Release.config's XDT to 10.
        // ApiUrl=https://localhost/api will get overridden by ConfigurationVariables to the
        // Squid variable's value. OrdersDb connectionString gets replaced similarly.
        File.WriteAllText(Path.Combine(stagingDir, "web.config"),
            "<?xml version=\"1.0\"?>\n" +
            "<configuration>\n" +
            "  <appSettings>\n" +
            "    <add key=\"ApiUrl\" value=\"https://localhost/api\" />\n" +
            "    <add key=\"MaxRetries\" value=\"3\" />\n" +
            "  </appSettings>\n" +
            "  <connectionStrings>\n" +
            "    <add name=\"OrdersDb\" connectionString=\"Server=local-db\" providerName=\"System.Data.SqlClient\" />\n" +
            "  </connectionStrings>\n" +
            "</configuration>\n");

        // web.Release.config — XDT transform. Only touches MaxRetries to avoid
        // overlapping with the ConfigurationVariables rewriter (which handles ApiUrl).
        File.WriteAllText(Path.Combine(stagingDir, "web.Release.config"),
            "<?xml version=\"1.0\"?>\n" +
            "<configuration xmlns:xdt=\"http://schemas.microsoft.com/XML-Document-Transform\">\n" +
            "  <appSettings>\n" +
            "    <add key=\"MaxRetries\" value=\"10\"\n" +
            "         xdt:Transform=\"SetAttributes(value)\" xdt:Locator=\"Match(key)\" />\n" +
            "  </appSettings>\n" +
            "</configuration>\n");

        // Plain HTML — no rewriters touch it; proves extraction without modification.
        File.WriteAllText(Path.Combine(stagingDir, "index.html"),
            "<!DOCTYPE html><html><body>OrderApi deployed</body></html>");

        // Nested binary — proves recursive extraction. Empty file is enough; we just
        // check existence.
        File.WriteAllText(Path.Combine(stagingDir, "bin", "OrderApi.dll"), "");

        // Zip it up.
        var zipPath = Path.Combine(Path.GetTempPath(), $"squid-iis-app-{Guid.NewGuid():N}.zip");
        ctx.RegisterTempDirForCleanup(zipPath);   // single-file path; Dispose handles non-dir cleanup gracefully
        ZipFile.CreateFromDirectory(stagingDir, zipPath);
        return zipPath;
    }

    // ── Helpers (mirror the ones in IISDeployRealHostE2ETests; duplicated here for
    //    isolation since this is a separate test class with a dedicated scope) ──

    private sealed class IISTestContext : IDisposable
    {
        private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
        private readonly List<string> _tempPathsToClean = new();
        private readonly List<string> _sentinelFilesToClean = new();
        private bool _markedClean;

        public IISTestContext()
        {
            SiteName = $"SquidIISRealWorld-{_suffix}";
            PoolName = $"SquidIISRealWorldPool-{_suffix}";
            PhysicalPath = Path.Combine(Path.GetTempPath(), $"squid-iis-realworld-{_suffix}");
            HttpPort = PickFreePort();
            Directory.CreateDirectory(PhysicalPath);
            _tempPathsToClean.Add(PhysicalPath);
        }

        public string SiteName { get; }
        public string PoolName { get; }
        public string PhysicalPath { get; }
        public string HttpPort { get; }

        public void RegisterTempDirForCleanup(string path) => _tempPathsToClean.Add(path);

        public string RegisterSentinelPath(string suffix)
        {
            var path = Path.Combine(Path.GetTempPath(), $"squid-iis-sentinel-{_suffix}-{suffix}.txt");
            _sentinelFilesToClean.Add(path);
            return path;
        }

        public void MarkClean() => _markedClean = true;

        public void Dispose()
        {
            if (OperatingSystem.IsWindows() && IsIISInstalled())
            {
                TryPowerShell($"Remove-Website -Name '{SiteName}' -ErrorAction SilentlyContinue");
                TryPowerShell($"Remove-WebAppPool -Name '{PoolName}' -ErrorAction SilentlyContinue");
            }

            foreach (var path in _tempPathsToClean)
            {
                try
                {
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    else if (File.Exists(path)) File.Delete(path);
                }
                catch { /* best-effort */ }
            }

            foreach (var sentinel in _sentinelFilesToClean)
            {
                try { if (File.Exists(sentinel)) File.Delete(sentinel); }
                catch { /* best-effort */ }
            }

            if (!_markedClean && OperatingSystem.IsWindows())
                Console.WriteLine($"[IISTestContext.Dispose] Composite test did NOT call MarkClean — Site='{SiteName}', Pool='{PoolName}'.");
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
        public const string CustomScriptsPreDeploy = "Squid.Action.CustomScripts.PreDeploy.ps1";
        public const string CustomScriptsPostDeploy = "Squid.Action.CustomScripts.PostDeploy.ps1";
        public const string ConfigurationVariablesEnabled = "Squid.Action.IISWebSite.ConfigurationVariables.Enabled";
        public const string ConfigurationTransformsEnabled = "Squid.Action.IISWebSite.ConfigurationTransforms.Enabled";
        public const string ConfigurationTransformsEnvironmentName = "Squid.Action.IISWebSite.ConfigurationTransforms.EnvironmentName";
        public const string SubstituteInFilesEnabled = "Squid.Action.IISWebSite.SubstituteInFiles.Enabled";
        public const string SubstituteInFilesTargetFiles = "Squid.Action.IISWebSite.SubstituteInFiles.TargetFiles";
        public const string StructuredConfigurationVariablesEnabled = "Squid.Action.IISWebSite.StructuredConfigurationVariables.Enabled";
        public const string StructuredConfigurationVariablesTargets = "Squid.Action.IISWebSite.StructuredConfigurationVariables.Targets";
        public const string PackageSourcePath = "Squid.Action.IISWebSite.Package.SourcePath";
        public const string PackagePurgeBeforeExtract = "Squid.Action.IISWebSite.Package.PurgeBeforeExtract";
    }

    private static DeploymentActionDto BuildAction(params (string Name, string Value)[] properties)
    {
        return new DeploymentActionDto
        {
            Id = 1,
            Name = "Composite real-world deploy",
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
            var r = RunPowerShell("(Get-WindowsFeature Web-WebServer -ErrorAction SilentlyContinue).Installed");
            return r.ExitCode == 0 && r.StdOut.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool IsMicrosoftWebXmlTransformAvailable()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var r = RunPowerShell(
                "$ok = $false; " +
                "try { Add-Type -AssemblyName 'Microsoft.Web.XmlTransform' -ErrorAction Stop; $ok = $true } catch {}; " +
                "if (-not $ok) { " +
                "  $probe = Get-ChildItem -Path \"$env:USERPROFILE\\.nuget\\packages\\microsoft.web.xdt\" -Recurse -Filter Microsoft.Web.XmlTransform.dll -ErrorAction SilentlyContinue | Select-Object -First 1; " +
                "  if ($probe) { try { Add-Type -Path $probe.FullName; $ok = $true } catch {} } " +
                "}; " +
                "Write-Host -NoNewline ($ok.ToString())");
            return r.ExitCode == 0 && r.StdOut.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string ReadAuthEnabledFlag(string siteName, string sectionName)
    {
        var ps =
            $"$output = & \"$env:SystemRoot\\system32\\inetsrv\\appcmd.exe\" list config '{siteName}' " +
            $"-section:system.webServer/security/authentication/{sectionName}; " +
            $"$output | Out-String";
        var output = RunPowerShell(ps).StdOut;
        var pattern = $"<{System.Text.RegularExpressions.Regex.Escape(sectionName)}\\b[^>]*\\benabled=\"(?<v>[^\"]+)\"";
        var match = System.Text.RegularExpressions.Regex.Match(output, pattern);
        return match.Success ? match.Groups["v"].Value : $"(no match in: {output.Trim()})";
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
        // Composite deploy may take a few seconds longer than per-feature tests because every
        // hook fires. 3 minutes is generous.
        process.WaitForExit(TimeSpan.FromMinutes(3));

        return new PsResult(process.ExitCode, stdout, stderr);
    }

    private sealed record PsResult(int ExitCode, string StdOut, string StdErr);
}
