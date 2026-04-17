using Squid.Message.Contracts.Tentacle;

namespace Squid.Tentacle.ScriptExecution.Logging;

public sealed record SequencedLogEntry(
    long Sequence,
    DateTimeOffset Occurred,
    ProcessOutputSource Source,
    string Text)
{
    public ProcessOutput ToProcessOutput() => new(Source, Text, Occurred);
}
