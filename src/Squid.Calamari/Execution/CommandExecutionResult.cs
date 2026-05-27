using Squid.Calamari.Pipeline;
using Squid.Calamari.ServiceMessages;

namespace Squid.Calamari.Execution;

public sealed class CommandExecutionResult
{
    public CommandExecutionResult(
        int exitCode,
        IReadOnlyList<OutputVariable>? outputVariables = null,
        IReadOnlyList<StepOutcome>? stepOutcomes = null)
    {
        ExitCode = exitCode;
        OutputVariables = outputVariables?.ToArray() ?? Array.Empty<OutputVariable>();
        StepOutcomes = stepOutcomes?.ToArray() ?? Array.Empty<StepOutcome>();
    }

    public int ExitCode { get; }

    public bool Succeeded => ExitCode == 0;

    public IReadOnlyList<OutputVariable> OutputVariables { get; }

    /// <summary>
    /// PR-5 — per-step structured outcomes emitted as the pipeline ran.
    /// Ordered by execution sequence (the index reflects pipeline order).
    /// Empty list for legacy callers / contexts that pre-date the
    /// <see cref="IStepOutcomeAwareContext"/> interface.
    /// </summary>
    public IReadOnlyList<StepOutcome> StepOutcomes { get; }
}
