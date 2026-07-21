using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Rendering.Exceptions;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Ssh.Packages;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Ssh.Rendering;

public sealed class SshIntentRenderer : IIntentRenderer
{
    public CommunicationStyle CommunicationStyle => CommunicationStyle.Ssh;

    public bool CanRender(ExecutionIntent intent) => intent is not null;

    public Task<ScriptExecutionRequest> RenderAsync(ExecutionIntent intent, IntentRenderContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(context);

        return intent switch
        {
            RunScriptIntent runScript => Task.FromResult(RenderRunScript(runScript, context)),
            DeployPackageIntent deployPackage => Task.FromResult(RenderDeployPackage(deployPackage, context)),
            _ => throw new IntentRenderingException(CommunicationStyle, intent, $"SshIntentRenderer has no native renderer for intent '{intent.Name}' ({intent.GetType().Name}).")
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
            ?? throw new IntentRenderingException(CommunicationStyle.Ssh, intent,
                $"No acquired package for {intent.Package.PackageId} v{intent.Package.Version}.");

        if (string.Equals(intent.InstallationDirectoryMode, "Custom", StringComparison.OrdinalIgnoreCase))
            PackageInstallationPath.ValidateCustomPath(intent.CustomInstallationDirectory, windowsRules: false);

        var archiveFileName = !string.IsNullOrWhiteSpace(acquired.LocalPath)
            ? Path.GetFileName(acquired.LocalPath)
            : string.Empty;
        var packageBaseDirectory = context.EffectiveVariables
            .FirstOrDefault(v => string.Equals(v.Name, SpecialVariables.Ssh.PackageBaseDirectory, StringComparison.OrdinalIgnoreCase))
            ?.Value
            ?? string.Empty;
        var script = SshPackageDeploymentScriptBuilder.Build(new SshPackageDeployScriptModel
        {
            ExpectedSha256 = acquired.Hash,
            Mode = intent.InstallationDirectoryMode,
            EnvironmentSegment = intent.PathSegments.EnvironmentName,
            ProjectSegment = intent.PathSegments.ProjectName,
            PackageSegment = intent.PathSegments.PackageId,
            VersionSegment = intent.PathSegments.Version,
            CustomInstallationDirectory = intent.CustomInstallationDirectory ?? string.Empty,
            PackageId = intent.Package.PackageId,
            PackageVersion = intent.Package.Version,
            ArchiveFileName = archiveFileName,
            PackageBaseDirectory = packageBaseDirectory
        });

        var variables = context.EffectiveVariables.ToList();
        void Set(string name, string value)
        {
            variables.RemoveAll(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
            variables.Add(new VariableDto { Name = name, Value = value });
        }
        Set(SpecialVariables.Action.PackageId, intent.Package.PackageId);
        Set(SpecialVariables.Action.PackageVersion, intent.Package.Version);
        Set(SpecialVariables.Action.PackageFeedId, intent.Package.FeedId);
        Set(SpecialVariables.Action.InstallationDirectoryMode, intent.InstallationDirectoryMode);
        Set(SpecialVariables.Action.CustomInstallationDirectory, intent.CustomInstallationDirectory ?? string.Empty);

        return new ScriptExecutionRequest
        {
            ScriptBody = script,
            Syntax = ScriptSyntax.Bash,
            StepName = intent.StepName,
            ActionName = intent.ActionName,
            ActionType = SpecialVariables.ActionTypes.TentaclePackage,
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Apply,
            PayloadKind = PayloadKind.None,
            Variables = variables,
            Machine = context.Target.Machine,
            EndpointContext = context.Target.EndpointContext,
            ServerTaskId = context.ServerTaskId,
            ReleaseVersion = context.ReleaseVersion,
            Timeout = intent.Timeout ?? context.StepTimeout,
            PackageReferences = new List<PackageAcquisitionResult> { acquired }
        };
    }
}
