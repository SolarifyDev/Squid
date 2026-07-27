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

        static string Var(List<VariableDto> vars, string name)
            => vars.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))?.Value
               ?? string.Empty;

        static bool Flag(List<VariableDto> vars, string name)
            => string.Equals(Var(vars, name), "True", StringComparison.OrdinalIgnoreCase)
               || string.Equals(Var(vars, name), "true", StringComparison.OrdinalIgnoreCase);

        static int IntVar(List<VariableDto> vars, string name)
            => int.TryParse(Var(vars, name), out var n) ? n : 0;

        var variablesList = context.EffectiveVariables.ToList();
        EnsureUnsupportedConfigRewriteFlagsAreOff(variablesList, intent);
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
            PackageBaseDirectory = packageBaseDirectory,
            SkipIfAlreadyInstalled = Flag(variablesList, "Squid.Action.Package.SkipIfAlreadyInstalled"),
            PurgeBeforeInstall = Flag(variablesList, "Squid.Action.Package.PurgeBeforeInstall"),
            PreservePaths = Var(variablesList, "Squid.Action.Package.PreservePaths"),
            RetentionCount = IntVar(variablesList, "Squid.Action.Package.RetentionCount"),
            UseCurrentPointer = Flag(variablesList, "Squid.Action.Package.UseCurrentPointer"),
            // Keep historical SSH behaviour: PreDeploy failure restores previous install.
            RollbackOnFailure = !string.Equals(Var(variablesList, "Squid.Action.Package.RollbackOnFailure"), "False", StringComparison.OrdinalIgnoreCase)
        });

        var variables = variablesList;
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
    private static void EnsureUnsupportedConfigRewriteFlagsAreOff(
        IReadOnlyList<VariableDto> variables,
        DeployPackageIntent intent)
    {
        static bool Enabled(IReadOnlyList<VariableDto> vars, string name)
        {
            var value = vars.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
            return string.Equals(value, "True", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        // SSH durable-install script currently stages/extracts/runs conventions only.
        // Config rewriters exist on the Calamari/Tentacle path; enabling them on SSH
        // must fail closed so operators never get a silent no-op rewrite.
        string[] unsupported =
        [
            // Four canonical rewrite enabled keys.
            SpecialVariables.Action.ConfigurationVariablesEnabled,
            "Squid.Action.SubstituteInFiles.Enabled",
            SpecialVariables.Action.JsonConfigVariablesEnabled,
            "Squid.Action.ConfigurationTransforms.Enabled",
            // Non-canonical Structured key still emitted by older deployments.
            SpecialVariables.Action.StructuredConfigurationVariablesEnabled,
            // IIS-legacy aliases that may still be set on shared action property bags.
            "Squid.Action.IISWebSite.ConfigurationVariables.Enabled",
            "Squid.Action.IISWebSite.SubstituteInFiles.Enabled",
            "Squid.Action.IISWebSite.StructuredConfigurationVariables.Enabled",
            "Squid.Action.IISWebSite.ConfigurationTransforms.Enabled"
        ];

        foreach (var flag in unsupported)
        {
            if (!Enabled(variables, flag))
                continue;

            throw new IntentRenderingException(
                CommunicationStyle.Ssh,
                intent,
                $"Deploy a Package feature '{flag}' is not supported on SSH targets. " +
                "Disable the flag or deploy via Tentacle/Calamari until SSH config rewrite is implemented.");
        }
    }
}

