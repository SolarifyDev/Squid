using Squid.Calamari.ServiceMessages;

namespace Squid.Calamari.Execution.Output;

public sealed class SquidServiceMessageOutputSink : IProcessOutputSink
{
    private readonly IReadOnlyList<IServiceMessageSink> _serviceMessageSinks;

    public SquidServiceMessageOutputSink(params IServiceMessageSink[] serviceMessageSinks)
        : this((IEnumerable<IServiceMessageSink>)serviceMessageSinks)
    {
    }

    public SquidServiceMessageOutputSink(IEnumerable<IServiceMessageSink> serviceMessageSinks)
    {
        _serviceMessageSinks = serviceMessageSinks?.ToArray() ?? Array.Empty<IServiceMessageSink>();
    }

    public void WriteStdout(string line)
    {
        if (!ServiceMessageParser.IsServiceMessage(line))
            return;

        var outputVariable = ServiceMessageParser.TryParse(line);
        if (outputVariable == null)
            return;

        foreach (var sink in _serviceMessageSinks)
            sink.WriteServiceMessage(outputVariable);
    }

    public void WriteStderr(string line)
    {
        // Intentionally no-op: Squid service messages are only parsed from stdout.
    }
}
