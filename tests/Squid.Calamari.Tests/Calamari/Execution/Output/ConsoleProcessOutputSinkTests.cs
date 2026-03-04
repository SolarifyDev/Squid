using Squid.Calamari.Execution.Output;

namespace Squid.Calamari.Tests.Calamari.Execution.Output;

[Collection("Console IO")]
public class ConsoleProcessOutputSinkTests
{
    [Fact]
    public void WriteStdout_ServiceMessage_IsSuppressed()
    {
        var sink = new ConsoleProcessOutputSink();
        using var stdout = new StringWriter();
        var originalOut = Console.Out;

        try
        {
            Console.SetOut(stdout);

            sink.WriteStdout("##squid[setVariable name='X' value='1']");
            sink.WriteStdout("visible");

            stdout.ToString().ShouldContain("visible");
            stdout.ToString().ShouldNotContain("##squid[setVariable");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void WriteStderr_WritesToConsoleError()
    {
        var sink = new ConsoleProcessOutputSink();
        using var stderr = new StringWriter();
        var originalErr = Console.Error;

        try
        {
            Console.SetError(stderr);

            sink.WriteStderr("boom");

            stderr.ToString().ShouldContain("boom");
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }
}
