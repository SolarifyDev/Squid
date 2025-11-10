using System.Collections.Generic;
using System.Threading.Tasks;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Domain.Deployments;

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
                foreach (var machine in targets)
                {
                    // 示例：根据 ActionType 组装命令文本
                    var cmd = new ActionCommand
                    {
                        DeploymentId = deploymentId,
                        StepId = step.Id,
                        ActionId = action.Id,
                        MachineId = machine.Id,
                        CommandText = $"run-{action.ActionType.ToLower()}",
                        Parameters = new Dictionary<string, string>
                        {
                            { "StepName", step.Name },
                            { "ActionName", action.Name },
                            { "MachineName", machine.Name }
                        }
                    };
                    commands.Add(cmd);
                }
            }
        }

        return Task.FromResult(commands);
    }
}
