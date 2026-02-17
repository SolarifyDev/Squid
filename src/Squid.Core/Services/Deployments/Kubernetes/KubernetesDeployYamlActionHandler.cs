using System.Text;
using Squid.Core.Services.Deployments;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.Deployments.Kubernetes;

public class KubernetesDeployYamlActionHandler : IActionHandler
{
    private const string DeployRawYamlActionType = "Squid.KubernetesDeployRawYaml";

    public string ActionType => DeployRawYamlActionType;

    public bool CanHandle(DeploymentActionDto action)
    {
        if (action == null) return false;

        return string.Equals(action.ActionType, DeployRawYamlActionType, StringComparison.OrdinalIgnoreCase);
    }

    public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var inlineYaml = GetPropertyValue(ctx.Action, "Squid.Action.KubernetesYaml.InlineYaml") ?? string.Empty;
        var syntaxStr = GetPropertyValue(ctx.Action, "Squid.Action.Script.Syntax");
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
            Syntax = syntax
        };

        return Task.FromResult(result);
    }

    private static string GetPropertyValue(DeploymentActionDto action, string propertyName)
    {
        return action.Properties?
            .FirstOrDefault(p => string.Equals(p.PropertyName, propertyName, StringComparison.OrdinalIgnoreCase))
            ?.PropertyValue;
    }
}
