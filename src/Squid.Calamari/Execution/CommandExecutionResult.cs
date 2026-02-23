using Squid.Calamari.ServiceMessages;

namespace Squid.Calamari.Execution;

public sealed class CommandExecutionResult
{
    public CommandExecutionResult(int exitCode, IReadOnlyList<OutputVariable>? outputVariables = null)
    {
        ExitCode = exitCode;
        OutputVariables = outputVariables?.ToArray() ?? Array.Empty<OutputVariable>();
    }

    public int ExitCode { get; }

    public bool Succeeded => ExitCode == 0;

    public IReadOnlyList<OutputVariable> OutputVariables { get; }
}
