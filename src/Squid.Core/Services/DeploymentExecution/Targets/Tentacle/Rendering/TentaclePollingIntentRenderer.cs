using System.IO;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Rendering.Exceptions;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Validation;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Tentacle.Rendering;

public sealed class TentaclePollingIntentRenderer : IIntentRenderer
{
    public CommunicationStyle CommunicationStyle => CommunicationStyle.TentaclePolling;

    public bool CanRender(ExecutionIntent intent) => intent is RunScriptIntent or DeployPackageIntent;

    public Task<ScriptExecutionRequest> RenderAsync(ExecutionIntent intent, IntentRenderContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(context);

        return intent switch
        {
            RunScriptIntent runScript => Task.FromResult(RenderRunScript(runScript, context)),
            DeployPackageIntent deployPackage => Task.FromResult(RenderDeployPackage(deployPackage, context)),
            _ => throw new IntentRenderingException(CommunicationStyle, intent, $"TentaclePollingIntentRenderer only supports RunScriptIntent and DeployPackageIntent, got '{intent.GetType().Name}'.")
        };
    }

    private static ScriptExecutionRequest RenderRunScript(RunScriptIntent intent, IntentRenderContext context)
    {
        return new ScriptExecutionRequest
        {
            ScriptBody = intent.ScriptBody,
            Syntax = intent.Syntax,
            StepName = intent.StepName,
            ActionName = intent.ActionName,
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Apply,
            PayloadKind = PayloadKind.None,
            Variables = context.EffectiveVariables.ToList(),
            Machine = context.Target.Machine,
            EndpointContext = context.Target.EndpointContext,
            ServerTaskId = context.ServerTaskId,
            ReleaseVersion = context.ReleaseVersion,
            Timeout = intent.Timeout ?? context.StepTimeout,
            PackageReferences = context.PackageReferences.ToList()
        };
    }

    private static ScriptExecutionRequest RenderDeployPackage(DeployPackageIntent intent, IntentRenderContext context)
    {
        var acquired = context.PackageReferences
            .FirstOrDefault(p => string.Equals(p.PackageId, intent.Package.PackageId, StringComparison.OrdinalIgnoreCase)
                                 && string.Equals(p.Version, intent.Package.Version, StringComparison.OrdinalIgnoreCase))
            ?? throw new IntentRenderingException(CommunicationStyle.TentaclePolling, intent,
                $"No acquired package for {intent.Package.PackageId} v{intent.Package.Version}.");

        var scriptSyntax = ResolveTargetScriptSyntax(context);

        if (string.Equals(intent.InstallationDirectoryMode, "Custom", StringComparison.OrdinalIgnoreCase))
            PackageInstallationPath.ValidateCustomPath(intent.CustomInstallationDirectory, windowsRules: scriptSyntax == ScriptSyntax.PowerShell);

        var variables = context.EffectiveVariables.ToList();
        Set(variables, SpecialVariables.Action.PackageId, intent.Package.PackageId);
        Set(variables, SpecialVariables.Action.PackageVersion, intent.Package.Version);
        Set(variables, SpecialVariables.Action.PackageFeedId, intent.Package.FeedId);
        Set(variables, SpecialVariables.Action.InstallationDirectoryMode, intent.InstallationDirectoryMode);
        Set(variables, SpecialVariables.Action.CustomInstallationDirectory, intent.CustomInstallationDirectory ?? string.Empty);
        Set(variables, "Squid.Action.Package.Path.Environment", intent.PathSegments.EnvironmentName);
        Set(variables, "Squid.Action.Package.Path.Project", intent.PathSegments.ProjectName);
        Set(variables, "Squid.Action.Package.Path.Package", intent.PathSegments.PackageId);
        Set(variables, "Squid.Action.Package.Path.Version", intent.PathSegments.Version);
        Set(variables, "Squid.Action.Package.Hash", acquired.Hash);
        Set(variables, "Squid.Action.Package.OriginalPath", $"./{Path.GetFileName(acquired.LocalPath)}");

        return new ScriptExecutionRequest
        {
            ScriptBody = string.Empty,
            Syntax = scriptSyntax,
            StepName = intent.StepName,
            ActionName = intent.ActionName,
            ActionType = SpecialVariables.ActionTypes.TentaclePackage,
            CalamariCommand = "deploy-package",
            ExecutionMode = ExecutionMode.PackagedPayload,
            ContextPreparationPolicy = ContextPreparationPolicy.Skip,
            PayloadKind = PayloadKind.PackageArchive,
            Variables = variables,
            Machine = context.Target.Machine,
            EndpointContext = context.Target.EndpointContext,
            ServerTaskId = context.ServerTaskId,
            ReleaseVersion = context.ReleaseVersion,
            Timeout = intent.Timeout ?? context.StepTimeout,
            PackageReferences = new List<PackageAcquisitionResult> { acquired }
        };
    }

    private static ScriptSyntax ResolveTargetScriptSyntax(IntentRenderContext context)
    {
        var os = context.EffectiveVariables
            .FirstOrDefault(v => string.Equals(v.Name, "Squid.Tentacle.OS", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        return WindowsOsStringHelper.IsWindows(os) ? ScriptSyntax.PowerShell : ScriptSyntax.Bash;
    }

    private static void Set(List<VariableDto> variables, string name, string value)
    {
        variables.RemoveAll(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
        variables.Add(new VariableDto { Name = name, Value = value });
    }
}
