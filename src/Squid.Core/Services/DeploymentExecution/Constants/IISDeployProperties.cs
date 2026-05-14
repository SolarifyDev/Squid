namespace Squid.Core.Services.DeploymentExecution;

/// <summary>
/// Action-property constants for the <c>Squid.DeployToIISWebSite</c> action type.
///
/// <para><b>Mirrors Octopus's <c>Octopus.Action.IISWebSite.*</c> taxonomy</b> from
/// <c>Octopus.Core/Variables/SpecialVariables.cs:637-674</c> and
/// <c>Calamari/Shared/Deployment/SpecialVariables.cs:126-142</c>. The names are kept
/// 1:1 with Octopus (only the <c>Octopus.</c> prefix is replaced with <c>Squid.</c>)
/// so operators familiar with Octopus IIS deploys can carry their variable spec
/// over verbatim. The <c>IISWebSite</c> infix is intentional and reserved — future
/// IIS-adjacent action types (IISWebDeploy, IISAzureFunction, …) get their own
/// namespaces so a sub-feature split never breaks a customer's existing variable
/// references.</para>
/// </summary>
internal static class IISDeployProperties
{
    // ── DeploymentType + sub-feature toggles ───────────────────────────────
    // A single Squid.DeployToIISWebSite action can deploy any combination of
    // Website / WebApplication / VirtualDirectory in one execution — exactly
    // mirrors Octopus's `IISWebSite_BeforePostDeploy.ps1:15-24` toggle layout.

    /// <summary>Optional hint, advisory only; the three CreateOrUpdate flags drive actual behaviour.</summary>
    internal const string DeploymentType = "Squid.Action.IISWebSite.DeploymentType";

    /// <summary>When <c>true</c>, the script creates / updates an IIS WebSite.</summary>
    internal const string CreateOrUpdateWebSite = "Squid.Action.IISWebSite.CreateOrUpdateWebSite";

    /// <summary>When <c>true</c>, the script creates / updates a Web Application under <see cref="WebApplicationWebSiteName"/>.</summary>
    internal const string WebApplicationCreateOrUpdate = "Squid.Action.IISWebSite.WebApplication.CreateOrUpdate";

    /// <summary>When <c>true</c>, the script creates / updates a Virtual Directory under <see cref="VirtualDirectoryWebSiteName"/>.</summary>
    internal const string VirtualDirectoryCreateOrUpdate = "Squid.Action.IISWebSite.VirtualDirectory.CreateOrUpdate";

    // ── WebSite properties ────────────────────────────────────────────────

    internal const string WebSiteName = "Squid.Action.IISWebSite.WebSiteName";
    internal const string ApplicationPoolName = "Squid.Action.IISWebSite.ApplicationPoolName";

    /// <summary>One of: ApplicationPoolIdentity, LocalService, LocalSystem, NetworkService, SpecificUser.</summary>
    internal const string ApplicationPoolIdentityType = "Squid.Action.IISWebSite.ApplicationPoolIdentityType";

    /// <summary>Required when <see cref="ApplicationPoolIdentityType"/> is <c>SpecificUser</c>. Format: <c>DOMAIN\user</c> or <c>.\user</c>.</summary>
    internal const string ApplicationPoolUsername = "Squid.Action.IISWebSite.ApplicationPoolUsername";

    /// <summary>Required when <see cref="ApplicationPoolIdentityType"/> is <c>SpecificUser</c>. Marked sensitive via the action property metadata.</summary>
    internal const string ApplicationPoolPassword = "Squid.Action.IISWebSite.ApplicationPoolPassword";

    /// <summary>One of: <c>v4.0</c>, <c>v2.0</c>, <c>No Managed Code</c>.</summary>
    internal const string ApplicationPoolFrameworkVersion = "Squid.Action.IISWebSite.ApplicationPoolFrameworkVersion";

    /// <summary>JSON array of <c>IISBinding</c> objects. See <c>IISBindingParser</c> for the schema.</summary>
    internal const string Bindings = "Squid.Action.IISWebSite.Bindings";

    /// <summary>Either <c>Merge</c> (preserve unmatched existing bindings) or <c>Replace</c> (default — clear all, apply configured).</summary>
    internal const string ExistingBindings = "Squid.Action.IISWebSite.ExistingBindings";

    /// <summary>Physical disk path that the WebSite serves from (e.g. <c>C:\inetpub\OrderApi</c>).</summary>
    internal const string WebRoot = "Squid.Action.IISWebSite.WebRoot";

    internal const string EnableAnonymousAuthentication = "Squid.Action.IISWebSite.EnableAnonymousAuthentication";
    internal const string EnableBasicAuthentication = "Squid.Action.IISWebSite.EnableBasicAuthentication";
    internal const string EnableWindowsAuthentication = "Squid.Action.IISWebSite.EnableWindowsAuthentication";

    /// <summary>Default <c>true</c>. When <c>false</c>, the pool is created/updated but left stopped.</summary>
    internal const string StartApplicationPool = "Squid.Action.IISWebSite.StartApplicationPool";

    /// <summary>Default <c>true</c>. When <c>false</c>, the WebSite is created/updated but left stopped.</summary>
    internal const string StartWebSite = "Squid.Action.IISWebSite.StartWebSite";

    // ── IIS-metabase locking + retry knobs (operator-tunable) ─────────────
    // These match Octopus's tuning surface so air-gapped operators with
    // performance-tuned IIS hosts can carry their existing values across.

    /// <summary>Maximum retry count when the IIS metabase mutex is held by another process. Default 5.</summary>
    internal const string MaxRetryFailures = "Squid.Action.IISWebSite.MaxRetryFailures";

    /// <summary>Sleep interval (seconds) between mutex-acquisition retries. Default random 1-3s.</summary>
    internal const string SleepBetweenRetryFailuresInSeconds = "Squid.Action.IISWebSite.SleepBetweenRetryFailuresInSeconds";

    // ── WebApplication properties (Phase 4 will activate these) ───────────

    internal const string WebApplicationWebSiteName = "Squid.Action.IISWebSite.WebApplication.WebSiteName";
    internal const string WebApplicationVirtualPath = "Squid.Action.IISWebSite.WebApplication.VirtualPath";
    internal const string WebApplicationPhysicalPath = "Squid.Action.IISWebSite.WebApplication.PhysicalPath";
    internal const string WebApplicationApplicationPoolName = "Squid.Action.IISWebSite.WebApplication.ApplicationPoolName";
    internal const string WebApplicationApplicationPoolIdentityType = "Squid.Action.IISWebSite.WebApplication.ApplicationPoolIdentityType";
    internal const string WebApplicationApplicationPoolUsername = "Squid.Action.IISWebSite.WebApplication.ApplicationPoolUsername";
    internal const string WebApplicationApplicationPoolPassword = "Squid.Action.IISWebSite.WebApplication.ApplicationPoolPassword";
    internal const string WebApplicationApplicationPoolFrameworkVersion = "Squid.Action.IISWebSite.WebApplication.ApplicationPoolFrameworkVersion";

    // ── VirtualDirectory properties (Phase 4 will activate these) ─────────

    internal const string VirtualDirectoryWebSiteName = "Squid.Action.IISWebSite.VirtualDirectory.WebSiteName";
    internal const string VirtualDirectoryVirtualPath = "Squid.Action.IISWebSite.VirtualDirectory.VirtualPath";
    internal const string VirtualDirectoryPhysicalPath = "Squid.Action.IISWebSite.VirtualDirectory.PhysicalPath";

    // ── .NET Configuration Variables (Phase 6 — Octopus ConfigurationVariables parity) ──
    //
    // Mirrors Octopus's `Octopus.Features.ConfigurationVariables` feature-flag from
    // `Calamari.Common/Plumbing/Variables/KnownVariables.cs:65`. When enabled (`"True"`),
    // the IIS deploy script walks every `*.config` file under <see cref="WebRoot"/>,
    // finds:
    //   - `&lt;appSettings&gt;&lt;add key="X" value="..."/&gt;`
    //   - `&lt;connectionStrings&gt;&lt;add name="X" connectionString="..."/&gt;`
    //   - `&lt;applicationSettings&gt;&lt;Class&gt;&lt;setting name="X"&gt;&lt;value&gt;...&lt;/value&gt;`
    // and replaces the value when a Squid variable named "X" exists in the deployment's
    // variable set. This is the operator-facing "Replace entries in .config files" checkbox
    // on the Octopus IIS deploy step UI's ".NET Configuration Variables" card.
    //
    // Setting this to "True" requires the action's full variable set to be shipped to the
    // agent (the rewriter needs to look up each Squid variable by name). The builder emits
    // a separate `$SquidVariables` hashtable in the preamble for this purpose.

    /// <summary>
    /// When <c>"True"</c>, the deploy script walks every <c>*.config</c> file under
    /// <see cref="WebRoot"/> and replaces matching <c>appSettings</c>, <c>connectionStrings</c>,
    /// and <c>applicationSettings</c> entries against the deployment's variable set. Empty
    /// or non-"True" value disables the feature (no .config files touched).
    /// </summary>
    internal const string ConfigurationVariablesEnabled = "Squid.Action.IISWebSite.ConfigurationVariables.Enabled";

    // ── Additional paths across all rewriters (P1-4, 1.6.9) ───────────────────
    //
    // Mirrors Octopus's `Octopus.Action.Package.AdditionalPaths`
    // (`ConfigurationVariablesBehaviour.cs:74-76`). Some apps store config files OUTSIDE
    // WebRoot — e.g. `<install>/config/*.config` sibling of `<install>/wwwroot/`. By
    // default the 4 rewriters (SubstituteInFiles, ConfigurationTransforms,
    // ConfigurationVariables, StructuredConfigurationVariables) scan WebRoot only;
    // setting AdditionalPaths extends the scan to those dirs too.
    //
    // Format: newline-separated absolute paths. Each path is scanned IN ADDITION to WebRoot.

    /// <summary>
    /// Newline-separated absolute directory paths. When set, all 4 config rewriters
    /// (SubstituteInFiles / ConfigurationTransforms / ConfigurationVariables /
    /// StructuredConfigurationVariables) scan WebRoot + each AdditionalPath.
    /// </summary>
    internal const string AdditionalPaths = "Squid.Action.IISWebSite.AdditionalPaths";

    // ── Deployment journal + SkipIfAlreadyInstalled (P0-2, 1.6.9) ─────────────
    //
    // Mirrors Octopus's `Octopus.Action.Package.SkipIfAlreadyInstalled` flag
    // (`KnownVariables.cs:86`) + `AlreadyInstalledConvention` + `IDeploymentJournalWriter`.
    // When enabled, the deploy script:
    //   1. Computes a fingerprint of the package (SHA256 of source path + WebRoot)
    //   2. Reads the deployment journal at `%PROGRAMDATA%\Squid\IISDeploy\journal\<siteName>.json`
    //   3. If the journal's last successful entry matches the current fingerprint → exit 0 with
    //      "already-installed; skipping" message. No extraction, no rewriting, no IIS reconfig
    //   4. Otherwise: deploy normally. Write a journal entry at end (success status)
    //
    // Reduces re-deploy cycle time from ~minutes to ~seconds for idempotent re-runs.

    /// <summary>
    /// When <c>"True"</c>, the deploy script short-circuits if the deployment journal records
    /// a prior successful deploy with the same package fingerprint + WebRoot. Default (unset /
    /// non-True) always re-runs the deploy fresh.
    /// </summary>
    internal const string PackageSkipIfAlreadyInstalled = "Squid.Action.IISWebSite.Package.SkipIfAlreadyInstalled";

    // ── Certificate auto-import + private-key ACL (P0-1, 1.6.9) ───────────────
    //
    // Operator-friendly HTTPS deploys without manual cert staging. Operator stores the PFX
    // contents as a base64 string in a Squid variable, references that variable here.
    // The deploy script imports the cert into `Cert:\LocalMachine\My`, writes the resulting
    // thumbprint into `$SquidVariables["<SiteThumbprintVariableName>.Thumbprint"]`, then the
    // Bindings JSON references it via `"certificateVariable": "<SiteThumbprintVariableName>"`
    // (Phase 2's existing cert-variable lookup path).
    //
    // After the bindings apply via `netsh http add sslcert`, the script grants the AppPool's
    // identity Read access on the cert's private key file. Mirrors Octopus's
    // `IisWebSiteBeforeDeployFeature.cs:24-89` (import) + `IisWebSiteAfterPostDeployFeature.cs:27-94`
    // (ACL).

    /// <summary>
    /// Base64-encoded PFX (PKCS#12) bytes. Operator-friendly path: store the PFX as a
    /// sensitive Squid variable, then reference via <c>#{MyPfxVariable}</c> here. When set,
    /// the deploy script imports the cert into <c>Cert:\LocalMachine\My</c> on the agent
    /// before binding resolution.
    /// </summary>
    internal const string CertificatePfxBase64 = "Squid.Action.IISWebSite.Certificate.PfxBase64";

    /// <summary>
    /// Import password for the PFX. Sensitive — operators set this as a Squid sensitive
    /// variable and reference here. Empty / unset when the PFX has no password.
    /// </summary>
    internal const string CertificatePfxPassword = "Squid.Action.IISWebSite.Certificate.PfxPassword";

    /// <summary>
    /// Logical variable name under which the imported cert's thumbprint is exposed at runtime.
    /// For example, if set to <c>"OrderApiCert"</c>, the imported thumbprint lands in
    /// <c>$SquidVariables["OrderApiCert.Thumbprint"]</c>. The operator's Bindings JSON can
    /// then reference it via <c>"certificateVariable": "OrderApiCert"</c> — Phase 2's existing
    /// cert-variable lookup path finds it automatically.
    /// </summary>
    internal const string CertificateThumbprintVariableName = "Squid.Action.IISWebSite.Certificate.ThumbprintVariableName";

    // ── Package extraction — pre-staged .zip / .nupkg deployment (Phase 10) ──
    //
    // Mirrors Octopus's package extraction step (`ExtractPackageToApplicationDirectoryConvention`
    // + related package-deploy plumbing). Operator pre-stages a deployable artifact on the
    // Tentacle agent (via prior `Squid.Script` step, fileserver mount, or operator manual stage)
    // and references it via <see cref="PackageSourcePath"/>. The deploy script extracts the
    // archive into <see cref="PackageExtractTo"/> (defaults to <see cref="WebRoot"/>), then
    // hands off to the rest of the pipeline.
    //
    // Phase 10 MVP supports operator-pre-staged paths only — operators stage the file via
    // their preferred mechanism. Future phases can add server-side feed-resolved package
    // shipping via <c>ScriptExecutionRequest.Files</c> for true "Squid downloads + ships"
    // end-to-end flow.
    //
    // Supported archive formats: <c>.zip</c> (via <c>Expand-Archive</c>) and <c>.nupkg</c>
    // (NuGet treated as zip; same Expand-Archive). Other formats (.tar.gz, .7z) deferred.

    /// <summary>
    /// Absolute path on the Tentacle agent to the archive to extract. Supported extensions:
    /// <c>.zip</c>, <c>.nupkg</c>. Operator stages the file via a prior <c>Squid.Script</c> step,
    /// fileserver mount, or pre-baked artifact location.
    /// </summary>
    internal const string PackageSourcePath = "Squid.Action.IISWebSite.Package.SourcePath";

    /// <summary>
    /// Target directory for extraction. When empty, defaults to <see cref="WebRoot"/> — the
    /// canonical "deploy package = web root" operator workflow. Operator can override to a
    /// staging directory if they need separate "extract here, then copy elsewhere" pipelines.
    /// </summary>
    internal const string PackageExtractTo = "Squid.Action.IISWebSite.Package.ExtractTo";

    /// <summary>
    /// When <c>"True"</c>, the extraction target directory is purged (deleted + recreated)
    /// BEFORE the archive is extracted. Matches Octopus's
    /// <c>Octopus.Action.Package.PurgeInstallationDirectory</c>. Use to guarantee old files
    /// from prior deploys don't survive — common with versioned web apps where an obsolete
    /// DLL could shadow a new one of the same name.
    /// </summary>
    internal const string PackagePurgeBeforeExtract = "Squid.Action.IISWebSite.Package.PurgeBeforeExtract";

    // ── Structured Configuration Variables — JSON/YAML leaf replacement (Phase 9) ──
    //
    // Mirrors Octopus's `Octopus.Features.JsonConfigurationVariables` feature
    // (`Calamari.Common/Features/Behaviours/StructuredConfigurationVariablesBehaviour.cs:13-35`).
    // Format-aware leaf replacement — walks JSON object structure, for each leaf checks if a
    // Squid variable matches the path (with both `:` and `.` separators supported, matching
    // .NET Core configuration conventions).
    //
    // Distinct from Phase 8 SubstituteInFiles:
    //   - SubstituteInFiles: TEXT-level `#{X}` token replacement in any file format
    //   - StructuredConfigurationVariables: STRUCTURE-AWARE leaf replacement (operator
    //     stores `ConnectionStrings:Default` as the variable name; the rewriter walks
    //     `appsettings.json` and replaces the `connectionStrings.default` leaf value)
    //
    // Phase 9 MVP supports JSON. YAML/properties files can be added in future phases
    // (Octopus's `StructuredConfigVariables.csproj` adds those format-specific parsers).

    /// <summary>
    /// When <c>"True"</c>, the deploy script walks each JSON file in <see cref="StructuredConfigurationVariablesTargets"/>
    /// and replaces leaf values where the JSON path matches a Squid variable name. Path forms:
    /// dot-separated (`Logging.LogLevel.Default`) and colon-separated (`Logging:LogLevel:Default`) both supported.
    /// </summary>
    internal const string StructuredConfigurationVariablesEnabled = "Squid.Action.IISWebSite.StructuredConfigurationVariables.Enabled";

    /// <summary>
    /// Newline-separated list of file paths / globs relative to <see cref="WebRoot"/>. Operator example:
    /// <c>"appsettings.json\nappsettings.Production.json"</c>. Matches Octopus's
    /// <c>Octopus.Action.Package.JsonConfigurationVariablesTargets</c>.
    /// </summary>
    internal const string StructuredConfigurationVariablesTargets = "Squid.Action.IISWebSite.StructuredConfigurationVariables.Targets";

    // ── SubstituteInFiles — variable replacement INSIDE files (Phase 8) ────
    //
    // Mirrors Octopus's `Octopus.Features.SubstituteInFiles` feature
    // (`Calamari.Common/Features/Behaviours/SubstituteInFilesBehaviour.cs:12-35`). When enabled,
    // the deploy script walks operator-specified files (newline-separated globs relative to
    // <see cref="WebRoot"/>) and replaces every <c>#{VariableName}</c> token with the matching
    // Squid variable's value. Unlike <see cref="ConfigurationVariablesEnabled"/> (which only
    // touches XML attributes inside <c>*.config</c>), SubstituteInFiles works on ANY text file
    // (JSON, YAML, properties, .txt, .config, anything).
    //
    // Pairs with the existing variable-shipping infrastructure: the builder already emits
    // <c>$SquidVariables</c> in the preamble (added in Phase 6 for ConfigurationVariables); this
    // feature reuses the same hashtable for token lookup.

    /// <summary>
    /// When <c>"True"</c>, the deploy script walks the file globs in <see cref="SubstituteInFilesTargetFiles"/>
    /// and replaces every <c>#{VariableName}</c> token (case-sensitive name match against the
    /// Squid variable set). Tokens with no matching variable are left as-is.
    /// </summary>
    internal const string SubstituteInFilesEnabled = "Squid.Action.IISWebSite.SubstituteInFiles.Enabled";

    /// <summary>
    /// Newline-separated (or comma-separated) list of file paths / globs relative to
    /// <see cref="WebRoot"/>. Operator example:
    /// <c>"appsettings.json\nappsettings.{env}.json\n*.yml"</c>. Absolute paths also supported.
    /// Matches Octopus's <c>Octopus.Action.SubstituteInFiles.TargetFiles</c>.
    /// </summary>
    internal const string SubstituteInFilesTargetFiles = "Squid.Action.IISWebSite.SubstituteInFiles.TargetFiles";

    // ── XML config transforms (Phase 7 — Octopus ConfigurationTransforms parity) ──
    //
    // Mirrors Octopus's `Octopus.Features.ConfigurationTransforms` feature
    // (`Calamari.Common/Features/Behaviours/ConfigurationTransformsBehaviour.cs:84-101`).
    // When enabled, the deploy script applies XDT (XML Document Transform) overlays to
    // every `*.config` file under <see cref="WebRoot"/>:
    //
    //   - <c>web.config</c> + <c>web.Release.config</c> → applies the Release transform
    //   - <c>web.config</c> + <c>web.{EnvironmentName}.config</c> → applies the env transform
    //
    // Operators can also specify explicit transforms via <see cref="ConfigurationTransformsAdditional"/>
    // (CSV of <c>transform.config =&gt; target.config</c> entries; matches Octopus's
    // <c>Octopus.Action.Package.AdditionalXmlConfigurationTransforms</c> format).
    //
    // The transform engine on the agent is <c>Microsoft.Web.XmlTransform.dll</c> (from the
    // <c>Microsoft.Web.Xdt</c> NuGet package). The script probes the GAC + common VS install
    // paths; if not found, the feature emits a clear remediation message and the script
    // continues (transform skipped — operator's `web.config` is left as-is).

    /// <summary>
    /// When <c>"True"</c>, the deploy script applies <c>*.Release.config</c> and
    /// <c>*.{EnvironmentName}.config</c> XDT transforms over their base files under
    /// <see cref="WebRoot"/>. Matches Octopus's
    /// <c>Octopus.Action.Package.AutomaticallyRunConfigurationTransformationFiles</c>.
    /// </summary>
    internal const string ConfigurationTransformsEnabled = "Squid.Action.IISWebSite.ConfigurationTransforms.Enabled";

    /// <summary>
    /// Environment name used for auto-transforms (<c>*.{EnvironmentName}.config</c>). Operators
    /// typically set this to <c>#{Squid.Environment.Name}</c> so the deploy picks the right env
    /// overlay automatically. Optional — when empty, only the Release transform is applied.
    /// </summary>
    internal const string ConfigurationTransformsEnvironmentName = "Squid.Action.IISWebSite.ConfigurationTransforms.EnvironmentName";

    /// <summary>
    /// Operator-specified explicit transforms as CSV of <c>source =&gt; target</c> entries
    /// (newline OR comma separated). Each transform applies independently of the auto-discovery
    /// flow. Example: <c>"connectionStrings.config =&gt; web.config, settings.{env}.config =&gt; settings.config"</c>.
    /// Matches Octopus's <c>Octopus.Action.Package.AdditionalXmlConfigurationTransforms</c>.
    /// </summary>
    internal const string ConfigurationTransformsAdditional = "Squid.Action.IISWebSite.ConfigurationTransforms.AdditionalTransforms";

    // ── Custom script slots (Phase 5 — Octopus CustomScripts parity) ──────
    //
    // Mirrors Octopus's <c>Octopus.Action.CustomScripts.{Stage}.{ext}</c> taxonomy from
    // <c>Calamari.Common/Plumbing/Variables/KnownVariables.cs:27-35</c>. Each property
    // value is the LITERAL SCRIPT BODY the operator wants executed at that stage
    // — not a file path. Operators commonly use these for:
    //   - PreDeploy: <c>Stop-WebAppPool $PoolName</c> so file replacement doesn't EBUSY
    //   - PostDeploy: <c>Start-WebAppPool $PoolName; Invoke-WebRequest http://localhost:$Port/health</c>
    //
    // The <c>.ps1</c> suffix is part of the key (not a file extension) and signals the
    // PowerShell syntax. Future syntaxes (<c>.sh</c> / <c>.py</c> / <c>.csx</c>) would
    // slot in as additional sibling constants when Squid supports non-Windows IIS.

    /// <summary>
    /// Operator PowerShell script that runs BEFORE the IIS configuration logic.
    /// Typical use: <c>Stop-WebAppPool $PoolName</c> to release file locks before deployment.
    /// Runs in an isolated scope but has access to <c>$SquidParameters</c>;
    /// <c>WebAdministration</c> is auto-imported.
    /// </summary>
    internal const string CustomScriptsPreDeploy = "Squid.Action.CustomScripts.PreDeploy.ps1";

    /// <summary>
    /// Operator PowerShell script that runs AFTER the IIS configuration logic completes
    /// successfully. Typical use: <c>Start-WebAppPool $PoolName; Invoke-WebRequest ...</c>
    /// for smoke-testing. Skipped if the IIS configure script threw (the throw aborts
    /// before reaching the PostDeploy hook). Runs in an isolated scope; <c>$SquidParameters</c>
    /// is available and <c>WebAdministration</c> is auto-imported.
    /// </summary>
    internal const string CustomScriptsPostDeploy = "Squid.Action.CustomScripts.PostDeploy.ps1";

    // ── Tentacle-runtime variables read by the script (NOT action properties) ─

    /// <summary>Injected by <c>TentacleEndpointVariableContributor</c>. The handler reads this to gate Windows-only execution.</summary>
    internal const string TentacleOS = "Squid.Tentacle.OS";
}
