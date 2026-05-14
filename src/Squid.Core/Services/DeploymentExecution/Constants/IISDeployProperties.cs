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
