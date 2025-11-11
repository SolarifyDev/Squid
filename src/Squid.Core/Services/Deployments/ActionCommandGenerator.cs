using System.Collections.Generic;
using System.Threading.Tasks;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Domain.Deployments;
using Squid.Message.Constants;

namespace Squid.Core.Services.Deployments;

public class ActionCommandGenerator : IActionCommandGenerator
{
    public Task<List<ActionCommand>> GenerateCommandsAsync(
        int deploymentId,
        List<Squid.Message.Domain.Deployments.Machine> targets,
        List<DeploymentStepDto> steps)
    {
        var commands = new List<ActionCommand>();

        foreach (var step in steps)
        {
            foreach (var action in step.Actions)
            {
                // 直接使用所有目标机器，因为DeploymentTargetFinder已经做了过滤
                foreach (var machine in targets)
                {
                    var cmd = GenerateCommandForAction(deploymentId, step, action, machine);
                    commands.Add(cmd);
                }
            }
        }

        return Task.FromResult(commands);
    }

    private ActionCommand GenerateCommandForAction(
        int deploymentId,
        DeploymentStepDto step,
        DeploymentActionDto action,
        Squid.Message.Domain.Deployments.Machine machine)
    {
        var parameters = new Dictionary<string, string>
        {
            { "StepName", step.Name },
            { "ActionName", action.Name },
            { "MachineName", machine.Name },
            { "MachineId", machine.Id.ToString() },
            { "StepOrder", step.StepOrder.ToString() },
            { "ActionOrder", action.ActionOrder.ToString() }
        };

        // 添加Action的Properties到参数中
        foreach (var prop in action.Properties)
        {
            parameters[prop.PropertyName] = prop.PropertyValue;
        }

        var commandText = GenerateCommandTextByActionType(action.ActionType, parameters);

        return new ActionCommand
        {
            DeploymentId = deploymentId,
            StepId = step.Id,
            ActionId = action.Id,
            MachineId = machine.Id,
            CommandText = commandText,
            Parameters = parameters
        };
    }

    private string GenerateCommandTextByActionType(string actionType, Dictionary<string, string> parameters)
    {
        return actionType switch
        {
            SpecialVariables.ActionTypes.Script => GenerateScriptCommand(parameters),
            SpecialVariables.ActionTypes.TentaclePackage => GeneratePackageCommand(parameters),
            SpecialVariables.ActionTypes.KubernetesDeployRawYaml => GenerateKubernetesCommand(parameters),
            SpecialVariables.ActionTypes.HttpRequest => GenerateHttpRequestCommand(parameters),
            SpecialVariables.ActionTypes.Manual => GenerateManualCommand(parameters),
            SpecialVariables.ActionTypes.DeployRelease => GenerateDeployReleaseCommand(parameters),
            SpecialVariables.ActionTypes.DeployIngress => GenerateDeployIngressCommand(parameters),

            // 向后兼容的简化匹配
            "script" => GenerateScriptCommand(parameters),
            "package" => GeneratePackageCommand(parameters),
            "iis" => GenerateIISCommand(parameters),
            "service" => GenerateServiceCommand(parameters),
            "powershell" => GeneratePowerShellCommand(parameters),
            "bash" => GenerateBashCommand(parameters),

            _ => GenerateGenericCommand(actionType, parameters)
        };
    }

    private string GenerateScriptCommand(Dictionary<string, string> parameters)
    {
        var scriptBody = parameters.GetValueOrDefault(SpecialVariables.Action.ScriptBody, "echo 'No script body'");
        var syntax = parameters.GetValueOrDefault(SpecialVariables.Action.ScriptSyntax, SpecialVariables.ScriptSyntax.PowerShell);

        return syntax.ToLower() switch
        {
            "powershell" => $"execute-script --syntax powershell --body \"{scriptBody}\"",
            "bash" => $"execute-script --syntax bash --body \"{scriptBody}\"",
            "python" => $"execute-script --syntax python --body \"{scriptBody}\"",
            "csharp" => $"execute-script --syntax csharp --body \"{scriptBody}\"",
            _ => $"execute-script --syntax {syntax} --body \"{scriptBody}\""
        };
    }

    private string GeneratePackageCommand(Dictionary<string, string> parameters)
    {
        var packageId = parameters.GetValueOrDefault(SpecialVariables.Action.PackageId, "unknown");
        var version = parameters.GetValueOrDefault(SpecialVariables.Action.PackageVersion, "latest");
        var feedId = parameters.GetValueOrDefault(SpecialVariables.Action.PackageFeedId, "");
        var customDir = parameters.GetValueOrDefault(SpecialVariables.Action.CustomInstallationDirectory, "");

        var command = $"deploy-package --id \"{packageId}\" --version \"{version}\"";

        if (!string.IsNullOrEmpty(feedId))
            command += $" --feed \"{feedId}\"";

        if (!string.IsNullOrEmpty(customDir))
            command += $" --directory \"{customDir}\"";

        return command;
    }

    private string GenerateKubernetesCommand(Dictionary<string, string> parameters)
    {
        var yaml = parameters.GetValueOrDefault(SpecialVariables.Action.KubernetesYaml, "");
        var namespace_ = parameters.GetValueOrDefault(SpecialVariables.Action.KubernetesNamespace, "default");

        return $"kubectl-apply --namespace \"{namespace_}\" --yaml \"{yaml}\"";
    }

    private string GenerateHttpRequestCommand(Dictionary<string, string> parameters)
    {
        var url = parameters.GetValueOrDefault(SpecialVariables.Action.HttpUrl, "");
        var method = parameters.GetValueOrDefault(SpecialVariables.Action.HttpMethod, "GET");
        var headers = parameters.GetValueOrDefault(SpecialVariables.Action.HttpHeaders, "");
        var body = parameters.GetValueOrDefault(SpecialVariables.Action.HttpBody, "");

        var command = $"http-request --url \"{url}\" --method \"{method}\"";

        if (!string.IsNullOrEmpty(headers))
            command += $" --headers \"{headers}\"";

        if (!string.IsNullOrEmpty(body))
            command += $" --body \"{body}\"";

        return command;
    }

    private string GenerateManualCommand(Dictionary<string, string> parameters)
    {
        var instructions = parameters.GetValueOrDefault(SpecialVariables.Action.ManualInstructions, "Manual intervention required");
        var responsibleTeams = parameters.GetValueOrDefault(SpecialVariables.Action.ManualResponsibleTeamIds, "");

        var command = $"manual-intervention --instructions \"{instructions}\"";

        if (!string.IsNullOrEmpty(responsibleTeams))
            command += $" --teams \"{responsibleTeams}\"";

        return command;
    }

    private string GenerateDeployReleaseCommand(Dictionary<string, string> parameters)
    {
        var projectId = parameters.GetValueOrDefault(SpecialVariables.Action.DeployReleaseProjectId, "");
        var version = parameters.GetValueOrDefault(SpecialVariables.Action.DeployReleaseVersion, "latest");
        var channelId = parameters.GetValueOrDefault(SpecialVariables.Action.DeployReleaseChannelId, "");

        var command = $"deploy-release --project \"{projectId}\" --version \"{version}\"";

        if (!string.IsNullOrEmpty(channelId))
            command += $" --channel \"{channelId}\"";

        return command;
    }

    private string GenerateDeployIngressCommand(Dictionary<string, string> parameters)
    {
        var ingressName = parameters.GetValueOrDefault("IngressName", "default-ingress");
        var namespace_ = parameters.GetValueOrDefault(SpecialVariables.Action.KubernetesNamespace, "default");

        return $"kubectl-ingress --name \"{ingressName}\" --namespace \"{namespace_}\"";
    }

    private string GenerateGenericCommand(string actionType, Dictionary<string, string> parameters)
    {
        Log.Information("Generating generic command for unsupported action type: {ActionType}", actionType);
        return $"run-{actionType.ToLower().Replace("squid.", "")}";
    }

    private string GenerateIISCommand(Dictionary<string, string> parameters)
    {
        var siteName = parameters.GetValueOrDefault("SiteName", "Default Web Site");
        return $"iis-deploy --site \"{siteName}\"";
    }

    private string GenerateServiceCommand(Dictionary<string, string> parameters)
    {
        var serviceName = parameters.GetValueOrDefault("ServiceName", "unknown");
        var action = parameters.GetValueOrDefault("ServiceAction", "restart");
        return $"service-{action} --name \"{serviceName}\"";
    }

    private string GeneratePowerShellCommand(Dictionary<string, string> parameters)
    {
        var script = parameters.GetValueOrDefault("PowerShellScript", "Write-Host 'No script'");
        return $"powershell -Command \"{script}\"";
    }

    private string GenerateBashCommand(Dictionary<string, string> parameters)
    {
        var script = parameters.GetValueOrDefault("BashScript", "echo 'No script'");
        return $"bash -c \"{script}\"";
    }
}
