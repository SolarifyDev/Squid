using System.Text;
using Squid.Core.Extensions;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Core.Services.DeploymentExecution.Handlers;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesDeployYamlActionHandler : IActionHandler
{
    public string ActionType => SpecialVariables.ActionTypes.KubernetesDeployRawYaml;

    public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var inlineYaml = ctx.Action.GetProperty(KubernetesRawYamlProperties.InlineYaml) ?? string.Empty;
        var syntaxStr = ctx.Action.GetProperty(SpecialVariables.Action.ScriptSyntax);
        var syntax = string.Equals(syntaxStr, ScriptSyntax.Bash.ToString(), StringComparison.OrdinalIgnoreCase)
            ? ScriptSyntax.Bash
            : ScriptSyntax.PowerShell;

        var files = new Dictionary<string, byte[]>();
        string scriptBody;

        if (!string.IsNullOrWhiteSpace(inlineYaml))
        {
            var yamlFileName = "inline-deployment.yaml";
            files[yamlFileName] = Encoding.UTF8.GetBytes(inlineYaml);

            var targetPath = syntax == ScriptSyntax.Bash ? $"./{yamlFileName}" : $".\\{yamlFileName}";
            scriptBody = KubernetesApplyCommandBuilder.Build(targetPath, ctx.Action, syntax);
        }
        else
        {
            var targetPath = syntax == ScriptSyntax.Bash ? "./content/" : ".\\content\\";
            scriptBody = KubernetesApplyCommandBuilder.Build(targetPath, ctx.Action, syntax);
        }

        var namespace_ = KubernetesYamlActionHandler.GetNamespaceFromAction(ctx.Action);
        scriptBody += KubernetesResourceWaitBuilder.BuildWaitScript(files, ctx.Action, namespace_, syntax);

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
