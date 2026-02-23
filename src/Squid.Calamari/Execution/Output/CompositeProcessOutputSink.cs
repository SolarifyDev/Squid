namespace Squid.Calamari.Execution.Output;

public sealed class CompositeProcessOutputSink : IProcessOutputSink
{
    private readonly IReadOnlyList<IProcessOutputSink> _sinks;

    public CompositeProcessOutputSink(params IProcessOutputSink[] sinks)
        : this((IEnumerable<IProcessOutputSink>)sinks)
    {
    }

    public CompositeProcessOutputSink(IEnumerable<IProcessOutputSink> sinks)
    {
        _sinks = sinks?.ToArray() ?? Array.Empty<IProcessOutputSink>();
    }

    public void WriteStdout(string line)
    {
        foreach (var sink in _sinks)
            sink.WriteStdout(line);
    }

    public void WriteStderr(string line)
    {
        foreach (var sink in _sinks)
            sink.WriteStderr(line);
    }
}
