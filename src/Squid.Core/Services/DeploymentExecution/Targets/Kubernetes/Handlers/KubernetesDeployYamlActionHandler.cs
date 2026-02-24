using System.Text;
using Squid.Core.Extensions;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesDeployYamlActionHandler : IActionHandler
{
    public DeploymentActionType ActionType => DeploymentActionType.KubernetesDeployRawYaml;

    public bool CanHandle(DeploymentActionDto action)
    {
        if (action == null) return false;

        return DeploymentActionTypeParser.Is(action.ActionType, ActionType);
    }

    public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var inlineYaml = ctx.Action.GetProperty("Squid.Action.KubernetesYaml.InlineYaml") ?? string.Empty;
        var syntaxStr = ctx.Action.GetProperty("Squid.Action.Script.Syntax");
        var syntax = string.Equals(syntaxStr, "Bash", StringComparison.OrdinalIgnoreCase)
            ? ScriptSyntax.Bash
            : ScriptSyntax.PowerShell;

        var files = new Dictionary<string, byte[]>();
        string scriptBody;

        if (!string.IsNullOrWhiteSpace(inlineYaml))
        {
            var yamlFileName = "inline-deployment.yaml";
            files[yamlFileName] = Encoding.UTF8.GetBytes(inlineYaml);

            scriptBody = syntax == ScriptSyntax.Bash
                ? $"kubectl apply -f \"./{yamlFileName}\""
                : $"kubectl apply -f \".\\{yamlFileName}\"";
        }
        else
        {
            scriptBody = syntax == ScriptSyntax.Bash
                ? "kubectl apply -f ./content/"
                : "kubectl apply -f .\\content\\";
        }

        var result = new ActionExecutionResult
        {
            ScriptBody = scriptBody,
            Files = files,
            CalamariCommand = null,
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Apply,
            PayloadKind = PayloadKind.None,
            Syntax = syntax
        };

        return Task.FromResult(result);
    }
}
