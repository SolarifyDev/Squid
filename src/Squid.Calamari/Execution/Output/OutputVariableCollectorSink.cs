using Squid.Calamari.ServiceMessages;

namespace Squid.Calamari.Execution.Output;

public sealed class OutputVariableCollectorSink : IServiceMessageSink
{
    private readonly List<OutputVariable> _outputVariables = new();

    public IReadOnlyList<OutputVariable> OutputVariables => _outputVariables;

    public void WriteServiceMessage(OutputVariable outputVariable)
    {
        _outputVariables.Add(outputVariable);
    }
}
