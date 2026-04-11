using System.Text;
using Squid.Core.Extensions;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Core.Services.DeploymentExecution.Handlers;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesDeployConfigMapActionHandler : IActionHandler
{
    private readonly ConfigMapResourceGenerator _generator = new();

    public string ActionType => SpecialVariables.ActionTypes.KubernetesDeployConfigMap;

    public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var properties = KubernetesPropertyParser.BuildPropertyDictionary(ctx.Action);

        if (!_generator.CanGenerate(properties))
            return Task.FromResult<ActionExecutionResult>(null);

        var yaml = _generator.Generate(properties);

        return Task.FromResult(new ActionExecutionResult
        {
            ScriptBody = KubernetesApplyCommandBuilder.Build("./configmap.yaml", ctx.Action, ScriptSyntax.Bash),
            Files = new Dictionary<string, byte[]> { ["configmap.yaml"] = Encoding.UTF8.GetBytes(yaml) },
            CalamariCommand = null,
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Apply,
            PayloadKind = PayloadKind.None,
            Syntax = ScriptSyntax.Bash
        });
    }

    /// <summary>
    /// Phase 9c.4 — direct intent emission. Bypasses the default <c>PrepareAsync</c> +
    /// <c>LegacyIntentAdapter</c> seam and produces a <see cref="KubernetesApplyIntent"/>
    /// with a stable semantic name (<c>k8s-apply</c>). The generated ConfigMap YAML is
    /// carried as a single <c>configmap.yaml</c> asset. Unconfigured or invalid actions
    /// produce an empty <c>YamlFiles</c> collection (a semantic no-op) instead of tripping
    /// the adapter's null-result guard.
    /// </summary>
    Task<ExecutionIntent> IActionHandler.DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var files = BuildYamlFiles(ctx.Action);
        var yamlFiles = DeploymentFileCollection.FromLegacyFiles(files).ToList();
        var namespace_ = KubernetesYamlActionHandler.GetNamespaceFromAction(ctx.Action);

        var (serverSide, fieldManager, forceConflicts) = KubernetesApplyIntentFactory.ReadServerSideApply(ctx.Action);
        var (objectStatusCheck, statusCheckTimeout) = KubernetesApplyIntentFactory.ReadObjectStatusCheck(ctx.Action);

        var intent = new KubernetesApplyIntent
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

        return Task.FromResult<ExecutionIntent>(intent);
    }

    private Dictionary<string, byte[]> BuildYamlFiles(DeploymentActionDto action)
    {
        if (action == null)
            return new Dictionary<string, byte[]>();

        var properties = KubernetesPropertyParser.BuildPropertyDictionary(action);

        if (!_generator.CanGenerate(properties))
            return new Dictionary<string, byte[]>();

        var yaml = _generator.Generate(properties);

        if (string.IsNullOrWhiteSpace(yaml))
            return new Dictionary<string, byte[]>();

        return new Dictionary<string, byte[]> { ["configmap.yaml"] = Encoding.UTF8.GetBytes(yaml) };
    }
}
