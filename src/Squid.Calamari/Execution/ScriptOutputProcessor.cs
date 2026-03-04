using Squid.Calamari.ServiceMessages;
using Squid.Calamari.Execution.Output;

namespace Squid.Calamari.Execution;

/// <summary>
/// Routes script output lines: service messages are parsed and suppressed from stdout;
/// plain log lines are forwarded to the console.
/// </summary>
public class ScriptOutputProcessor
{
    private readonly OutputVariableCollectorSink _outputVariableCollector;
    private readonly CompositeProcessOutputSink _compositeSink;

    public ScriptOutputProcessor()
    {
        _outputVariableCollector = new OutputVariableCollectorSink();

        _compositeSink = new CompositeProcessOutputSink(
            new SquidServiceMessageOutputSink(_outputVariableCollector),
            new ConsoleProcessOutputSink());
    }

    public IReadOnlyList<OutputVariable> OutputVariables => _outputVariableCollector.OutputVariables;

    internal IProcessOutputSink OutputSink => _compositeSink;

    public void ProcessLine(string? line, bool isError = false)
    {
        if (line == null)
            return;

        if (isError)
            _compositeSink.WriteStderr(line);
        else
            _compositeSink.WriteStdout(line);
    }
}
