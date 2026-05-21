using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Validation;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Tentacle.Handlers;

/// <summary>
/// Handles the <c>Squid.DeployToIISWebSite</c> action — emits a <see cref="RunScriptIntent"/>
/// carrying the assembled PowerShell payload (server-generated <c>$SquidParameters</c>
/// preamble + verbatim Octopus-mirrored deploy script body).
///
/// <para>Auto-registered as <see cref="IActionHandler"/> via <c>IScopedDependency</c>
/// scan; <see cref="ActionHandlerRegistry"/> picks it up by <see cref="ActionType"/>.</para>
///
/// <para><b>OS guard</b> — IIS only runs on Windows Tentacles. If the target machine's
/// runtime-capabilities cache reports a non-Windows OS we throw a descriptive error at
/// dispatch time rather than letting the agent fail mid-script with a cryptic "the term
/// 'Get-WindowsFeature' is not recognised" message. If the OS is unknown (cache miss
/// for a brand-new Tentacle that hasn't been health-checked yet), we proceed optimistically
/// — the agent-side script's <c>Get-WindowsFeature Web-WebServer</c> probe is the final
/// authority and will produce a clear error if the agent isn't actually Windows.</para>
/// </summary>
public class IISDeployActionHandler : IActionHandler
{
    public string ActionType => SpecialVariables.ActionTypes.DeployToIISWebSite;

    /// <summary>
    /// Plan-time static requirements: the action only runs on Windows targets
    /// that have a PowerShell-family shell available. Declared here so
    /// <c>DeploymentPlanner</c> can reject incompatible targets at preview /
    /// release-creation time — the operator sees the blocked target with a
    /// clear reason instead of the deploy failing at dispatch.
    ///
    /// <para>The dispatch-time guard <see cref="EnsureWindowsTentacleTarget"/>
    /// remains as belt-and-braces: if the cache is stale or the machine
    /// changed OS between preview and execute, the guard catches it before
    /// the agent script tries <c>Get-WindowsFeature</c> on a non-Windows host.</para>
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> StaticRequirements { get; } =
        CapabilityRequirements.Empty
            .Require(CapabilityKeys.OsSlot, CapabilityKeys.Os.Windows)
            .Require(CapabilityKeys.Shell.PowerShell, CapabilityKeys.Present);

    Task<ExecutionIntent> IActionHandler.DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        EnsureWindowsTentacleTarget(ctx);

        // Pass ctx.Variables so the builder emits the $SquidVariables hashtable —
        // required by the .NET Configuration Variables feature (Phase 6) which looks up
        // each <appSettings/add@key> + <connectionStrings/add@name> + <applicationSettings/setting@name>
        // in $SquidVariables and replaces the in-file value when a Squid variable matches.
        var scriptBody = IISDeployScriptBuilder.Build(ctx.Action, ctx.Variables);

        var intent = new RunScriptIntent
        {
            Name = "deploy-to-iis-website",
            StepName = ctx.Step?.Name ?? string.Empty,
            ActionName = ctx.Action?.Name ?? string.Empty,
            ScriptBody = scriptBody,
            Syntax = Message.Models.Deployments.Execution.ScriptSyntax.PowerShell,
            // The IIS script self-contains all helpers it needs (mutex / retry / SetUp-ApplicationPool
            // / netsh / appcmd) — porting Octopus's PS1 verbatim. Squid's runtime bundle is bash-first
            // and would just add noise, so we opt out.
            InjectRuntimeBundle = false
        };

        return Task.FromResult<ExecutionIntent>(intent);
    }

    /// <summary>
    /// Rejects dispatch when the target machine is known to NOT be a Windows Tentacle.
    /// "Known non-Windows" = the runtime-capabilities cache has a value for
    /// <c>Squid.Tentacle.OS</c> and that value isn't <c>Windows</c>. "Unknown" (no value
    /// in the cache yet) is permitted — the script will fail loudly on the agent if so.
    /// </summary>
    private static void EnsureWindowsTentacleTarget(ActionExecutionContext ctx)
    {
        var osVariable = ctx.Variables?
            .FirstOrDefault(v => string.Equals(v.Name, IISDeployProperties.TentacleOS, StringComparison.OrdinalIgnoreCase));

        if (osVariable == null || string.IsNullOrEmpty(osVariable.Value))
            return;   // unknown — proceed optimistically (see XML doc above)

        if (string.Equals(osVariable.Value, "Windows", StringComparison.OrdinalIgnoreCase))
            return;   // confirmed Windows — proceed

        var stepName = ctx.Step?.Name ?? "(unknown)";
        var actionName = ctx.Action?.Name ?? "(unknown)";

        throw new InvalidOperationException(
            $"Action '{actionName}' (type '{SpecialVariables.ActionTypes.DeployToIISWebSite}') in step '{stepName}' " +
            $"requires a Windows Tentacle target. The configured target reports '{IISDeployProperties.TentacleOS}'='{osVariable.Value}'. " +
            $"To deploy to IIS, configure a Windows Tentacle (Polling or Listening) and assign it the role this step targets. " +
            $"If you believe the target IS Windows, run a health check against it so the runtime-capabilities cache refreshes.");
    }
}
