using Squid.Calamari.ServiceMessages;

namespace Squid.Calamari.Scripting;

public sealed class ScriptExecutionResult
{
    public ScriptExecutionResult(int exitCode, IReadOnlyList<OutputVariable>? outputVariables = null)
    {
        ExitCode = exitCode;
        OutputVariables = outputVariables?.ToArray() ?? Array.Empty<OutputVariable>();
    }

    public int ExitCode { get; }

    public bool Succeeded => ExitCode == 0;

    public IReadOnlyList<OutputVariable> OutputVariables { get; }
}
