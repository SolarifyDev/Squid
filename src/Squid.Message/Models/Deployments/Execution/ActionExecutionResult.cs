namespace Squid.Message.Models.Deployments.Execution;

public class ActionExecutionResult
{
    public string ActionName { get; set; }

    public string ActionType { get; set; }

    public string ScriptBody { get; set; }

    public Dictionary<string, byte[]> Files { get; set; } = new();

    public string CalamariCommand { get; set; }

    public ExecutionMode ExecutionMode { get; set; } = ExecutionMode.Unspecified;

    public ContextPreparationPolicy ContextPreparationPolicy { get; set; } = ContextPreparationPolicy.Unspecified;

    public ExecutionLocation ExecutionLocation { get; set; } = ExecutionLocation.Unspecified;

    public ExecutionBackend ExecutionBackend { get; set; } = ExecutionBackend.Unspecified;

    public PayloadKind PayloadKind { get; set; } = PayloadKind.Unspecified;

    public RunnerKind RunnerKind { get; set; } = RunnerKind.Unspecified;

    public ScriptSyntax Syntax { get; set; } = ScriptSyntax.Bash;

    public Dictionary<string, string> ActionProperties { get; set; }

    public Dictionary<string, string> OutputVariables { get; set; } = new();

    public HashSet<string> SensitiveOutputVariableNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string ManualInterventionInstructions { get; set; }

    public List<string> Warnings { get; set; } = new();

    public ExecutionMode ResolveExecutionMode()
    {
        if (ExecutionMode == ExecutionMode.Unspecified)
            throw new InvalidOperationException("ActionExecutionResult.ExecutionMode must be explicitly set.");

        return ExecutionMode;
    }

    public ContextPreparationPolicy ResolveContextPreparationPolicy()
    {
        if (ContextPreparationPolicy != ContextPreparationPolicy.Unspecified)
            return ContextPreparationPolicy;

        return ResolveExecutionMode() == ExecutionMode.PackagedPayload
            ? ContextPreparationPolicy.Skip
            : ContextPreparationPolicy.Apply;
    }
}

public enum ScriptSyntax
{
    PowerShell = 0,
    Bash = 1,
    CSharp = 2,
    FSharp = 3,
    Python = 4
}
