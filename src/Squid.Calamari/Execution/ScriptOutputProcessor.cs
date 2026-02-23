using Squid.Calamari.ServiceMessages;

namespace Squid.Calamari.Execution;

/// <summary>
/// Routes script output lines: service messages are parsed and suppressed from stdout;
/// plain log lines are forwarded to the console.
/// </summary>
public class ScriptOutputProcessor
{
    private readonly List<OutputVariable> _outputVariables = new();

    public IReadOnlyList<OutputVariable> OutputVariables => _outputVariables;

    public void ProcessLine(string? line, bool isError = false)
    {
        if (line == null)
            return;

        if (!isError && ServiceMessageParser.IsServiceMessage(line))
        {
            var variable = ServiceMessageParser.TryParse(line);

            if (variable != null)
                _outputVariables.Add(variable);

            return;
        }

        if (isError)
            Console.Error.WriteLine(line);
        else
            Console.WriteLine(line);
    }
}
