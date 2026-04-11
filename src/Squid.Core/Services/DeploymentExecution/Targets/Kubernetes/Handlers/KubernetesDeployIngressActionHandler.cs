using Squid.Core.Extensions;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Core.Services.DeploymentExecution.Handlers;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesDeployIngressActionHandler : IActionHandler
{
    private readonly KubernetesIngressActionYamlGenerator _generator = new();

    public string ActionType => SpecialVariables.ActionTypes.KubernetesDeployIngress;

    public async Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var files = await _generator.GenerateAsync(ctx.Step, ctx.Action, ct).ConfigureAwait(false);

        if (files == null || files.Count == 0)
            return null;

        var result = new ActionExecutionResult
        {
            ScriptBody = KubernetesApplyCommandBuilder.Build("./ingress.yaml", ctx.Action, ScriptSyntax.Bash),
            Files = files,
            CalamariCommand = null,
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Apply,
            PayloadKind = PayloadKind.None,
            Syntax = ScriptSyntax.Bash
        };

        return result;
    }

    /// <summary>
    /// Phase 9c.4 — direct intent emission. Bypasses the default <c>PrepareAsync</c> +
    /// <c>LegacyIntentAdapter</c> seam and produces a <see cref="KubernetesApplyIntent"/>
    /// with a stable semantic name (<c>k8s-apply</c>). The generated Ingress YAML is
    /// carried as a single <c>ingress.yaml</c> asset. Unconfigured or invalid actions
    /// produce an empty <c>YamlFiles</c> collection (a semantic no-op) instead of tripping
    /// the adapter's null-result guard.
    /// </summary>
    async Task<ExecutionIntent> IActionHandler.DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var files = await _generator.GenerateAsync(ctx.Step, ctx.Action, ct).ConfigureAwait(false)
            ?? new Dictionary<string, byte[]>();
        var yamlFiles = DeploymentFileCollection.FromLegacyFiles(files).ToList();
        var namespace_ = KubernetesYamlActionHandler.GetNamespaceFromAction(ctx.Action);

        var (serverSide, fieldManager, forceConflicts) = KubernetesApplyIntentFactory.ReadServerSideApply(ctx.Action);
        var (objectStatusCheck, statusCheckTimeout) = KubernetesApplyIntentFactory.ReadObjectStatusCheck(ctx.Action);

        return new KubernetesApplyIntent
        {
            Name = "k8s-apply",
            StepName = ctx.Step?.Name ?? string.Empty,
            ActionName = ctx.Action?.Name ?? string.Empty,
            YamlFiles = yamlFiles,
            Assets = yamlFiles,
            Namespace = namespace_,
            Syntax = ScriptSyntax.Bash,
            ServerSideApply = serverSide,
            FieldManager = fieldManager,
            ForceConflicts = forceConflicts,
            ObjectStatusCheck = objectStatusCheck,
            StatusCheckTimeoutSeconds = statusCheckTimeout
        };
    }
}
