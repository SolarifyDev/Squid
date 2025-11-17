using System.Collections.Generic;
using System.Threading.Tasks;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Domain.Deployments;

namespace Squid.Core.Services.Deployments;

public class ActionCommand
{
    public int DeploymentId { get; set; }
    public int StepId { get; set; }
    public int ActionId { get; set; }
    public int MachineId { get; set; }
    public string CommandText { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new();
}

public interface IActionCommandGenerator : IScopedDependency
{
    Task<List<ActionCommand>> GenerateCommandsAsync(
        int deploymentId,
        List<Squid.Message.Domain.Deployments.Machine> targets,
        List<DeploymentStepDto> steps);
}
