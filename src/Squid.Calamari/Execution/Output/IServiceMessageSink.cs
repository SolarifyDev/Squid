using Squid.Calamari.ServiceMessages;

namespace Squid.Calamari.Execution.Output;

public interface IServiceMessageSink
{
    void WriteServiceMessage(OutputVariable outputVariable);
}
