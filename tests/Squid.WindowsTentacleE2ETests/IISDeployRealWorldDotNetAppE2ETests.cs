using System.IO.Compression;
using Squid.Core.Services.DeploymentExecution.Tentacle.Handlers;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// Composite end-to-end test: stages a realistic <c>.NET</c> application artifact and
/// exercises EVERY operator-facing feature of <c>Squid.DeployToIISWebSite</c> across a
/// matrix of canonical operator workflows. Mirrors the way an Octopus operator would set
/// up a single deploy step in real production:
///
/// <list type="number">
///   <item><b>Package extraction</b> with <c>PurgeBeforeExtract</c></item>
///   <item><b>Inline + packaged PreDeploy / PostDeploy</b> custom-script hooks (1.6.9 P1-3)</item>
///   <item><b>SubstituteInFiles</b> — <c>#{X}</c> tokens AND filter form <c>#{X | ToUpper}</c> (1.6.9 P0-3)</item>
///   <item><b>ConfigurationTransforms (XDT)</b> — <c>web.Release.config</c> overlay</item>
///   <item><b>ConfigurationVariables</b> — <c>appSettings/add@key</c> + <c>connectionStrings/add@name</c></item>
///   <item><b>StructuredConfigurationVariables</b> — JSON nested-leaf replacement using BOTH
///         <c>:</c> and <c>.</c> separators, AND 1.6.9 type-preserving substitution
///         (int / bool / array) (P0-4)</item>
///   <item><b>AdditionalPaths</b> — config file OUTSIDE <c>WebRoot</c> still rewritten (1.6.9 P1-4)</item>
///   <item><b>Deployment journal idempotence</b> — second deploy with <c>SkipIfAlreadyInstalled=True</c>
///         short-circuits without re-extracting (1.6.9 P0-2)</item>
///   <item><b>IIS configure</b> — site, app pool, bindings, auth toggles</item>
/// </list>
///
/// <para>The test asserts the END STATE — every file's content, IIS metabase entries, sentinel
/// contents, journal file — proving the eleven phases compose correctly into a single coherent
/// operator-facing deploy. This is the canonical "does the full action work end-to-end on a
/// real .NET artifact with all 1.6.x features ON" question.</para>
///
/// <para><b>Why a composite test on top of per-feature tests?</b> The per-feature tests in
/// <see cref="IISDeployRealHostE2ETests"/> exercise each feature in isolation. Real operators
/// turn every checkbox ON at once. The composite test catches ordering bugs, variable-shadowing
/// between rewriters, type-coercion side effects on adjacent JSON keys, etc. — bugs that pass
/// the per-feature tier but break in production.</para>
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
    public void RealWorld_FullDotNetAppDeploy_AllFeaturesAndIdempotence_ComposeCorrectly()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;
        if (!IsMicrosoftWebXmlTransformAvailable()) return;

        using var ctx = new IISTestContext();

        // ──── STAGE 1: Build the operator-facing .NET app artifact ─────────────────
        //
        // Constructs an in-memory representation of a realistic ASP.NET Core 8.0
        // deployment package containing:
        //   - appsettings.json (with tokens for SubstituteInFiles + nested keys for
        //     StructuredConfigurationVariables, including TYPED values: int / bool / array
        //     to exercise 1.6.9 P0-4 type-preservation)
        //   - web.config (with appSettings + connectionStrings for ConfigurationVariables)
        //   - web.Release.config (XDT transforms)
        //   - PreDeploy.ps1 / PostDeploy.ps1 INSIDE the package (1.6.9 P1-3 packaged scripts)
        //   - index.html (proves recursive extraction works)
        //   - bin/ subdir with a fake DLL (proves nested-directory extraction)
        var artifactPath = StageNetAppArtifact(ctx);

        // Pre-stage a stale marker in WebRoot — Package.PurgeBeforeExtract must wipe it
        // before extraction. Proves the purge contract end-to-end.
        var stalePath = Path.Combine(ctx.PhysicalPath, "stale-from-previous-deploy.txt");
        File.WriteAllText(stalePath, "should be purged");

        // Stage a config file OUTSIDE the WebRoot — AdditionalPaths must scan it (1.6.9 P1-4).
        // Real-world use case: app stores secrets in `<install>/config/secrets.json` next to
        // `<install>/wwwroot/`, NOT under WebRoot. Without AdditionalPaths the rewriters would
        // miss it entirely.
        var externalConfigDir = Path.Combine(Path.GetTempPath(), $"squid-iis-external-{Guid.NewGuid():N}");
        Directory.CreateDirectory(externalConfigDir);
        ctx.RegisterTempDirForCleanup(externalConfigDir);
        var externalConfigPath = Path.Combine(externalConfigDir, "secrets.json");
        File.WriteAllText(externalConfigPath,
            "{\n" +
            "  \"ApiKey\": \"#{ExternalApiKey}\",\n" +
            "  \"Region\": \"#{Region | ToUpper}\"\n" +
            "}\n");

        // Sentinel paths for the 4 custom-script hooks (inline + packaged × pre + post).
        var inlinePreSentinel = ctx.RegisterSentinelPath("realworld-inline-predeploy");
        var inlinePostSentinel = ctx.RegisterSentinelPath("realworld-inline-postdeploy");
        var packagedPreSentinel = ctx.RegisterSentinelPath("realworld-packaged-predeploy");
        var packagedPostSentinel = ctx.RegisterSentinelPath("realworld-packaged-postdeploy");

        // Override the staging-time sentinel placeholders in the packaged scripts so they
        // write to test-controlled paths.
        OverridePackagedScriptSentinels(artifactPath, packagedPreSentinel, packagedPostSentinel);

        // ──── STAGE 2: Define the operator's Squid variable set ───────────────────
        //
        // Six variable groups, each consumed by a different feature path:
        //
        //   Group A — SubstituteInFiles plain tokens (#{X}):
        //     - AppVersion        → "1.4.2"
        //     - EnvironmentTag    → "Production"
        //     - ExternalApiKey    → "sk_live_xxx" (replaces token in external secrets.json via AdditionalPaths)
        //
        //   Group B — SubstituteInFiles FILTER form (#{X | Filter}) — 1.6.9 P0-3:
        //     - Region            → "us-east-1"  (rendered as "US-EAST-1" via | ToUpper)
        //
        //   Group C — ConfigurationVariables (XML <add key=...>/<add name=...> in web.config):
        //     - ApiUrl            → URL
        //     - OrdersDb          → connstr
        //
        //   Group D — StructuredConfigurationVariables nested leaves in appsettings.json,
        //             string values:
        //     - Logging:LogLevel:Default → "Debug"      (colon path — .NET Core style)
        //     - ConnectionStrings.CacheDb → "...cache"  (dot path — .NET Framework style)
        //
        //   Group E — StructuredConfigurationVariables 1.6.9 P0-4 type preservation:
        //     - MaxThreads        → "8"          (parsed as int, NOT "8" string)
        //     - EnableSwagger     → "false"      (parsed as bool, NOT "false" string)
        //     - Hosts             → '["a","b"]'  (parsed as JSON array, NOT "...string")
        //
        //   Group F — Variables intentionally unused (proves the variable set tolerates noise):
        //     - UnusedVariable    → "ignored"
        var variables = new List<VariableDto>
        {
            // Group A
            new() { Name = "AppVersion", Value = "1.4.2" },
            new() { Name = "EnvironmentTag", Value = "Production" },
            new() { Name = "ExternalApiKey", Value = "sk_live_xxx" },
            // Group B (filter form)
            new() { Name = "Region", Value = "us-east-1" },
            // Group C
            new() { Name = "ApiUrl", Value = "https://api.prod.example.com/v2" },
            new() { Name = "OrdersDb", Value = "Server=prod-db-cluster;Database=Orders;Integrated Security=True" },
            // Group D
            new() { Name = "Logging:LogLevel:Default", Value = "Debug" },
            new() { Name = "ConnectionStrings.CacheDb", Value = "Redis=cache-prod-1.local:6379" },
            // Group E — 1.6.9 P0-4 type preservation
            new() { Name = "MaxThreads", Value = "8" },
            new() { Name = "EnableSwagger", Value = "false" },
            new() { Name = "Hosts", Value = "[\"a.example.com\",\"b.example.com\"]" },
            // Group F (noise)
            new() { Name = "UnusedVariable", Value = "ignored" }
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
            // — Custom scripts (Phase 5 + 1.6.9 P1-3 packaged scripts) —
            (Property.CustomScriptsPreDeploy,
                $"Set-Content -Path '{inlinePreSentinel}' -Value 'inline-predeploy-fired'"),
            (Property.CustomScriptsPostDeploy,
                $"$site = Get-Website -Name '{ctx.SiteName}'; " +
                $"Set-Content -Path '{inlinePostSentinel}' -Value \"inline-postdeploy-saw-site-state=$($site.State)\""),
            // — SubstituteInFiles (Phase 8 + 1.6.9 P0-3 filter form) —
            (Property.SubstituteInFilesEnabled, "True"),
            (Property.SubstituteInFilesTargetFiles, "appsettings.json"),
            // — ConfigurationTransforms / XDT (Phase 7) —
            (Property.ConfigurationTransformsEnabled, "True"),
            (Property.ConfigurationTransformsEnvironmentName, "Production"),
            // — ConfigurationVariables (Phase 6) —
            (Property.ConfigurationVariablesEnabled, "True"),
            // — StructuredConfigurationVariables / JSON (Phase 9 + 1.6.9 P0-4 type preservation) —
            (Property.StructuredConfigurationVariablesEnabled, "True"),
            (Property.StructuredConfigurationVariablesTargets, "appsettings.json"),
            // — 1.6.9 P1-4: AdditionalPaths (rewriters scan external dir too) —
            (Property.AdditionalPaths, externalConfigDir),
            // — 1.6.9 P0-2: Deployment-journal short-circuit. We turn this ON to capture
            //   that the first run writes a Success entry — the SECOND run later in this
            //   test (stage 14) verifies the short-circuit.
            (Property.PackageSkipIfAlreadyInstalled, "True"));

        // ──── STAGE 4: Build + run the FIRST deploy script ────────────────────────
        var script = IISDeployScriptBuilder.Build(action, variables);
        var firstRun = RunPowerShell(script);

        firstRun.ExitCode.ShouldBe(0,
            customMessage:
                "Full real-world deploy failed — at least one of the 11 features broke. " +
                "Triage steps:\n" +
                $"  1. Inspect STDOUT for which phase wrote the last Write-Host line\n" +
                $"  2. Check sentinel files: inline pre/post {inlinePreSentinel} / {inlinePostSentinel}\n" +
                $"  3. `Get-Website -Name '{ctx.SiteName}'` to check IIS metabase\n" +
                $"  4. `Get-ChildItem '{ctx.PhysicalPath}' -Recurse` to inspect WebRoot contents\n\n" +
                $"STDOUT:\n{firstRun.StdOut}\n\nSTDERR:\n{firstRun.StdErr}");

        // ──── STAGE 5: Verify Package Extraction ──────────────────────────────────
        File.Exists(stalePath).ShouldBeFalse(
            customMessage: $"Stale file '{stalePath}' from prior deploy survived PurgeBeforeExtract — purge contract broken.");

        File.Exists(Path.Combine(ctx.PhysicalPath, "index.html")).ShouldBeTrue(
            customMessage: $"index.html missing from WebRoot — Expand-IISPackage didn't extract into '{ctx.PhysicalPath}'.");

        File.Exists(Path.Combine(ctx.PhysicalPath, "bin", "OrderApi.dll")).ShouldBeTrue(
            customMessage: "Nested bin/OrderApi.dll missing — recursive extraction broken.");

        // ──── STAGE 6: Verify all 4 Custom-Script hooks fired in the right order ──
        File.Exists(inlinePreSentinel).ShouldBeTrue(
            customMessage: $"Inline PreDeploy sentinel '{inlinePreSentinel}' missing — configured PreDeploy didn't run.");
        File.ReadAllText(inlinePreSentinel).Trim().ShouldBe("inline-predeploy-fired");

        File.Exists(packagedPreSentinel).ShouldBeTrue(
            customMessage: $"Packaged PreDeploy sentinel '{packagedPreSentinel}' missing — PreDeploy.ps1 inside the package didn't fire (1.6.9 P1-3).");

        File.Exists(inlinePostSentinel).ShouldBeTrue(
            customMessage: "Inline PostDeploy sentinel missing.");
        File.Exists(packagedPostSentinel).ShouldBeTrue(
            customMessage: "Packaged PostDeploy sentinel missing (1.6.9 P1-3).");

        // ──── STAGE 7: Verify SubstituteInFiles plain tokens (#{X}) ───────────────
        var renderedAppSettings = File.ReadAllText(Path.Combine(ctx.PhysicalPath, "appsettings.json"));

        renderedAppSettings.ShouldContain("\"OrderApi v1.4.2\"",
            customMessage: $"#{{AppVersion}} token NOT replaced. appsettings.json:\n{renderedAppSettings}");
        renderedAppSettings.ShouldContain("\"Production\"",
            customMessage: "#{EnvironmentTag} token NOT replaced.");
        renderedAppSettings.ShouldNotContain("#{AppVersion}");
        renderedAppSettings.ShouldNotContain("#{EnvironmentTag}");

        // ──── STAGE 7b: Verify SubstituteInFiles FILTER form (1.6.9 P0-3) ─────────
        // `#{Region | ToUpper}` should render the lower-case "us-east-1" as "US-EAST-1".
        renderedAppSettings.ShouldContain("\"US-EAST-1\"",
            customMessage:
                $"#{{Region | ToUpper}} filter did NOT apply — expected 'US-EAST-1' (upper-cased). " +
                $"appsettings.json content:\n{renderedAppSettings}");
        renderedAppSettings.ShouldNotContain("us-east-1",
            customMessage: "Lower-case 'us-east-1' still present — filter chain didn't apply.");
        renderedAppSettings.ShouldNotContain("#{Region",
            customMessage: "#{Region | ToUpper} token left intact — filter parsing broke.");

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

        // ──── STAGE 8b: Verify 1.6.9 P0-4 JSON type preservation ──────────────────
        // MaxThreads variable is "8" (string). After type-preserving substitution the JSON
        // value MUST be the unquoted integer `8`, not the string `"8"`. Real .NET apps
        // bind to `int MaxThreads { get; set; }` — a string-typed value here triggers
        // JsonException at startup.
        renderedAppSettings.ShouldMatch("\"MaxThreads\":\\s*8\\b",
            customMessage:
                $"MaxThreads must be unquoted int '8' (NOT '\"8\"'). 1.6.9 P0-4 type preservation broken. " +
                $"appsettings.json content:\n{renderedAppSettings}");
        renderedAppSettings.ShouldNotContain("\"MaxThreads\": \"8\"",
            customMessage: "MaxThreads is a quoted string — type-preservation failed for int.");

        // EnableSwagger — "false" string → unquoted bool false
        renderedAppSettings.ShouldMatch("\"EnableSwagger\":\\s*false\\b",
            customMessage:
                $"EnableSwagger must be unquoted bool 'false'. .NET would throw JsonException if it sees '\"false\"' string. " +
                $"appsettings.json content:\n{renderedAppSettings}");
        renderedAppSettings.ShouldNotContain("\"EnableSwagger\": \"false\"");

        // Hosts — JSON array '["a.example.com","b.example.com"]' must be a real array
        renderedAppSettings.ShouldMatch("\"Hosts\":\\s*\\[",
            customMessage:
                $"Hosts must be a real JSON array. .NET binds 'string[] Hosts' — string-encoded array throws. " +
                $"appsettings.json content:\n{renderedAppSettings}");
        renderedAppSettings.ShouldContain("\"a.example.com\"");
        renderedAppSettings.ShouldContain("\"b.example.com\"");

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

        // ──── STAGE 11: Verify AdditionalPaths external rewrite (1.6.9 P1-4) ──────
        // The secrets.json file at externalConfigDir is OUTSIDE WebRoot. Without AdditionalPaths
        // the rewriters would have skipped it entirely.
        var renderedExternalSecrets = File.ReadAllText(externalConfigPath);
        renderedExternalSecrets.ShouldContain("\"ApiKey\": \"sk_live_xxx\"",
            customMessage:
                $"ExternalApiKey token NOT replaced in external file '{externalConfigPath}'. " +
                $"1.6.9 P1-4 AdditionalPaths broke — rewriters not scanning external dir. Content:\n{renderedExternalSecrets}");
        renderedExternalSecrets.ShouldContain("\"Region\": \"US-EAST-1\"",
            customMessage: "Filter form `#{Region | ToUpper}` didn't render in external file — confirms AdditionalPaths threads both plain + filter substitution.");

        // ──── STAGE 12: Verify IIS Configure ──────────────────────────────────────
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

        // ──── STAGE 13: Verify inline PostDeploy fired AFTER IIS Configure ────────
        var inlinePostContent = File.ReadAllText(inlinePostSentinel).Trim();
        inlinePostContent.ShouldBe("inline-postdeploy-saw-site-state=Started",
            customMessage:
                $"Inline PostDeploy observed site state '{inlinePostContent}' — expected '...=Started'. " +
                "If 'Stopped'/empty, PostDeploy ran BEFORE IIS configure (ordering broken).");

        // ──── STAGE 14: Journal idempotence — SECOND deploy must short-circuit ────
        // 1.6.9 P0-2: with SkipIfAlreadyInstalled=True, the next deploy with the same
        // package fingerprint + WebRoot should write "Skipping this deploy" and exit 0
        // WITHOUT re-extracting, re-rewriting, or re-running any custom scripts.
        //
        // The witness: re-stage a stale marker that would normally be purged by stage 1's
        // PurgeBeforeExtract, then run again. The marker MUST survive the second run if
        // the journal short-circuit fired correctly.
        var idempotenceWitness = Path.Combine(ctx.PhysicalPath, "should-survive-second-run.txt");
        File.WriteAllText(idempotenceWitness, "second-run-marker");

        File.Delete(inlinePreSentinel);   // clear so we can detect re-fire
        File.Delete(inlinePostSentinel);

        var secondRun = RunPowerShell(script);

        secondRun.ExitCode.ShouldBe(0,
            customMessage:
                $"Second (idempotent) deploy failed. SkipIfAlreadyInstalled should short-circuit when journal " +
                $"matches. STDOUT:\n{secondRun.StdOut}\n\nSTDERR:\n{secondRun.StdErr}");

        secondRun.StdOut.ShouldContain("SkipIfAlreadyInstalled: prior deploy",
            customMessage:
                "Second deploy did NOT short-circuit — journal idempotence broken. The deploy re-extracted " +
                "and re-ran the rewriters, defeating the purpose of SkipIfAlreadyInstalled.");

        File.Exists(idempotenceWitness).ShouldBeTrue(
            customMessage:
                "Second deploy purged the WebRoot — proves the short-circuit didn't fire. " +
                "The witness file should have survived because no extraction should have happened.");

        File.Exists(inlinePreSentinel).ShouldBeFalse(
            customMessage: "Inline PreDeploy fired on second deploy — short-circuit should bypass ALL custom scripts.");

        File.Exists(inlinePostSentinel).ShouldBeFalse(
            customMessage: "Inline PostDeploy fired on second deploy — short-circuit should bypass ALL custom scripts.");

        // Verify a journal file was written under %PROGRAMDATA%\Squid\IISDeploy\journal\
        var journalDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Squid", "IISDeploy", "journal");
        var journalFile = Path.Combine(journalDir, $"{ctx.SiteName}.json");
        ctx.RegisterTempDirForCleanup(journalFile);
        File.Exists(journalFile).ShouldBeTrue(
            customMessage:
                $"Journal entry '{journalFile}' not written by first deploy. " +
                $"Without a journal entry, SkipIfAlreadyInstalled has nothing to compare against.");

        var journalText = File.ReadAllText(journalFile);
        journalText.ShouldContain("\"Status\": \"Success\"",
            customMessage: $"Journal entry has wrong Status — expected 'Success'. Content:\n{journalText}");
        journalText.ShouldContain("\"Fingerprint\"",
            customMessage: "Journal entry missing Fingerprint field.");

        ctx.MarkClean();
    }

    /// <summary>
    /// Stages a realistic <c>.NET</c> application package on disk and returns the path to the
    /// resulting <c>.zip</c> archive. The package contents are designed to exercise every
    /// rewriter in the IIS deploy pipeline including the 1.6.9 additions:
    ///
    /// <list type="bullet">
    ///   <item><c>appsettings.json</c> — contains <c>#{X}</c> tokens (SubstituteInFiles plain),
    ///         a <c>#{X | Filter}</c> token (1.6.9 P0-3), nested string values
    ///         (StructuredConfigurationVariables string), AND typed leaves
    ///         (<c>MaxThreads</c>, <c>EnableSwagger</c>, <c>Hosts</c>) that 1.6.9 P0-4
    ///         type-preservation must keep unquoted</item>
    ///   <item><c>web.config</c> — <c>&lt;appSettings/add@key&gt;</c> entries for
    ///         ApiUrl (ConfigurationVariables) and MaxRetries (XDT); plus a
    ///         <c>&lt;connectionStrings/add@name&gt;</c> for OrdersDb</item>
    ///   <item><c>web.Release.config</c> — XDT overlay that changes MaxRetries to 10</item>
    ///   <item><c>PreDeploy.ps1</c> / <c>PostDeploy.ps1</c> at package root — Octopus
    ///         <c>PackagedScriptBehaviour</c> parity (1.6.9 P1-3). Sentinel paths are
    ///         placeholders that the caller rewrites after staging.</item>
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

        // appsettings.json — designed for SubstituteInFiles (plain + filter), nested-string
        // StructuredConfigurationVariables, AND typed StructuredConfigurationVariables (1.6.9 P0-4).
        File.WriteAllText(Path.Combine(stagingDir, "appsettings.json"),
            "{\n" +
            "  \"ApplicationName\": \"OrderApi v#{AppVersion}\",\n" +
            "  \"Environment\": \"#{EnvironmentTag}\",\n" +
            "  \"DeployRegion\": \"#{Region | ToUpper}\",\n" +
            "  \"Logging\": {\n" +
            "    \"LogLevel\": {\n" +
            "      \"Default\": \"Information\"\n" +
            "    }\n" +
            "  },\n" +
            "  \"ConnectionStrings\": {\n" +
            "    \"CacheDb\": \"Server=cache-localhost\"\n" +
            "  },\n" +
            "  \"MaxThreads\": 4,\n" +
            "  \"EnableSwagger\": true,\n" +
            "  \"Hosts\": [\"localhost\"]\n" +
            "}\n");

        // web.config — for ConfigurationVariables (appSettings + connectionStrings).
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

        // web.Release.config — XDT transform. Only touches MaxRetries.
        File.WriteAllText(Path.Combine(stagingDir, "web.Release.config"),
            "<?xml version=\"1.0\"?>\n" +
            "<configuration xmlns:xdt=\"http://schemas.microsoft.com/XML-Document-Transform\">\n" +
            "  <appSettings>\n" +
            "    <add key=\"MaxRetries\" value=\"10\"\n" +
            "         xdt:Transform=\"SetAttributes(value)\" xdt:Locator=\"Match(key)\" />\n" +
            "  </appSettings>\n" +
            "</configuration>\n");

        // Packaged PreDeploy.ps1 — 1.6.9 P1-3. Operator ships hooks inside the package.
        // We write a placeholder sentinel path that the caller (StageNetAppArtifact's
        // invoker) rewrites after zipping. The placeholder convention is unique enough
        // that no real script body would contain it.
        File.WriteAllText(Path.Combine(stagingDir, "PreDeploy.ps1"),
            "Set-Content -Path '__PACKAGED_PREDEPLOY_SENTINEL__' -Value 'packaged-predeploy-fired'\n");

        File.WriteAllText(Path.Combine(stagingDir, "PostDeploy.ps1"),
            "Set-Content -Path '__PACKAGED_POSTDEPLOY_SENTINEL__' -Value 'packaged-postdeploy-fired'\n");

        // Plain HTML — no rewriters touch it; proves extraction without modification.
        File.WriteAllText(Path.Combine(stagingDir, "index.html"),
            "<!DOCTYPE html><html><body>OrderApi deployed</body></html>");

        // Nested binary — proves recursive extraction. Empty file is enough; we just
        // check existence.
        File.WriteAllText(Path.Combine(stagingDir, "bin", "OrderApi.dll"), "");

        // Zip it up.
        var zipPath = Path.Combine(Path.GetTempPath(), $"squid-iis-app-{Guid.NewGuid():N}.zip");
        ctx.RegisterTempDirForCleanup(zipPath);
        ZipFile.CreateFromDirectory(stagingDir, zipPath);
        return zipPath;
    }

    /// <summary>
    /// Rewrites the placeholder sentinel paths embedded inside the packaged PreDeploy.ps1 /
    /// PostDeploy.ps1 (staged with literal <c>__PACKAGED_PREDEPLOY_SENTINEL__</c> placeholders)
    /// to the test-controlled paths. Done in-place on the zip — unzips, edits, re-zips —
    /// because we can't know the sentinel paths at staging time (they depend on the test's
    /// <c>IISTestContext</c> instance which exists at runtime).
    /// </summary>
    private static void OverridePackagedScriptSentinels(string zipPath, string preSentinel, string postSentinel)
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"squid-iis-zip-rewrite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, workDir);

            var preScript = Path.Combine(workDir, "PreDeploy.ps1");
            File.WriteAllText(preScript,
                File.ReadAllText(preScript).Replace("__PACKAGED_PREDEPLOY_SENTINEL__", preSentinel));

            var postScript = Path.Combine(workDir, "PostDeploy.ps1");
            File.WriteAllText(postScript,
                File.ReadAllText(postScript).Replace("__PACKAGED_POSTDEPLOY_SENTINEL__", postSentinel));

            File.Delete(zipPath);
            ZipFile.CreateFromDirectory(workDir, zipPath);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort */ }
        }
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
        // 1.6.9 additions
        public const string AdditionalPaths = "Squid.Action.IISWebSite.AdditionalPaths";
        public const string PackageSkipIfAlreadyInstalled = "Squid.Action.IISWebSite.Package.SkipIfAlreadyInstalled";
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
        // hook fires + we run it TWICE for idempotence. 3 minutes is generous.
        process.WaitForExit(TimeSpan.FromMinutes(3));

        return new PsResult(process.ExitCode, stdout, stderr);
    }

    private sealed record PsResult(int ExitCode, string StdOut, string StdErr);
}
