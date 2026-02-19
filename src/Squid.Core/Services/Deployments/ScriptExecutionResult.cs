namespace Squid.Core.Services.Deployments;

public class ScriptExecutionResult
{
    public bool Success { get; set; }
    public List<string> LogLines { get; set; } = new();
    public int ExitCode { get; set; }
}
