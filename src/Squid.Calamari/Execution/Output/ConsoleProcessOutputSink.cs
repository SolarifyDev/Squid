using Squid.Calamari.ServiceMessages;

namespace Squid.Calamari.Execution.Output;

public sealed class ConsoleProcessOutputSink : IProcessOutputSink
{
    public void WriteStdout(string line)
    {
        // Service messages are handled by a dedicated sink and should stay out of user-facing stdout.
        if (ServiceMessageParser.IsServiceMessage(line))
            return;

        Console.WriteLine(line);
    }

    public void WriteStderr(string line)
    {
        Console.Error.WriteLine(line);
    }
}
