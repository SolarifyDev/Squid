namespace Squid.Message.Models.Deployments.Execution;

public class ActionExecutionResult
{
    public string ScriptBody { get; set; }

    public Dictionary<string, byte[]> Files { get; set; } = new();

    public string CalamariCommand { get; set; }

    public ScriptSyntax Syntax { get; set; } = ScriptSyntax.PowerShell;

    public Dictionary<string, string> OutputVariables { get; set; } = new();
}

public enum ScriptSyntax
{
    PowerShell = 0,
    Bash = 1
}
