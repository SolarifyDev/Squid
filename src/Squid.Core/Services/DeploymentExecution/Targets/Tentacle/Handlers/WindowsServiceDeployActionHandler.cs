using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Validation;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Tentacle.Handlers;

/// <summary>
/// Handles the <c>Squid.DeployWindowsService</c> action for Windows Tentacle targets.
/// </summary>
public class WindowsServiceDeployActionHandler : IActionHandler
{
    public string ActionType => SpecialVariables.ActionTypes.DeployWindowsService;

    public IReadOnlyDictionary<string, IReadOnlySet<string>> StaticRequirements { get; } =
        CapabilityRequirements.Empty
            .Require(CapabilityKeys.OsSlot, CapabilityKeys.Os.Windows)
            .Require(CapabilityKeys.Shell.PowerShell, CapabilityKeys.Present);

    Task<ExecutionIntent> IActionHandler.DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        EnsureWindowsTentacleTarget(ctx);

        var scriptBody = WindowsServiceDeployScriptBuilder.Build(ctx.Action, ctx.Variables, ctx.SelectedPackages);

        var intent = new RunScriptIntent
        {
            Name = "deploy-windows-service",
            StepName = ctx.Step?.Name ?? string.Empty,
            ActionName = ctx.Action?.Name ?? string.Empty,
            ScriptBody = scriptBody,
            Syntax = ScriptSyntax.PowerShell,
            InjectRuntimeBundle = false
        };

        return Task.FromResult<ExecutionIntent>(intent);
    }

    private static void EnsureWindowsTentacleTarget(ActionExecutionContext ctx)
    {
        var osVariable = ctx.Variables?
            .FirstOrDefault(v => string.Equals(v.Name, WindowsServiceDeployProperties.TentacleOS, StringComparison.OrdinalIgnoreCase));

        if (osVariable == null || string.IsNullOrEmpty(osVariable.Value))
            return;

        if (WindowsOsStringHelper.IsWindows(osVariable.Value))
            return;

        var stepName = ctx.Step?.Name ?? "(unknown)";
        var actionName = ctx.Action?.Name ?? "(unknown)";

        throw new InvalidOperationException(
            $"Action '{actionName}' (type '{SpecialVariables.ActionTypes.DeployWindowsService}') in step '{stepName}' " +
            $"requires a Windows Tentacle target. The configured target reports '{WindowsServiceDeployProperties.TentacleOS}'='{osVariable.Value}'. " +
            $"To deploy a Windows service, configure a Windows Tentacle (Polling or Listening) and assign it the role this step targets. " +
            $"If you believe the target IS Windows, run a health check against it so the runtime-capabilities cache refreshes.");
    }

    internal static bool LooksLikeWindowsOsString(string osValue)
        => WindowsOsStringHelper.IsWindows(osValue);
}
