using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution;

public class ScriptExecutionRequest
{
    public string ScriptBody { get; set; }
    public Dictionary<string, byte[]> Files { get; set; } = new();
    public string CalamariCommand { get; set; }
    public List<VariableDto> Variables { get; set; }
    public Persistence.Entities.Deployments.Machine Machine { get; set; }
    public string ReleaseVersion { get; set; }
    public byte[] CalamariPackageBytes { get; set; }
}
