using Squid.Calamari.Execution.Output;

namespace Squid.Calamari.Tests.Calamari.Execution.Output;

[Collection("Process Globals")]
public class ConsoleProcessOutputSinkTests
{
    [Fact]
    public void WriteStdout_ServiceMessage_IsForwardedSoTheServerCanSeeIt()
    {
        // This process's stdout is the wire back to the server: the Tentacle captures it into
        // the script's log lines, which are the server's ONLY source of output variables.
        // Suppressing the line here silently lost every output variable a Calamari-run script
        // emitted, while the direct (non-Calamari) path forwarded the identical line.
        var sink = new ConsoleProcessOutputSink();
        using var stdout = new StringWriter();
        var originalOut = Console.Out;

        try
        {
            Console.SetOut(stdout);

            sink.WriteStdout("##squid[setVariable name='X' value='1']");
            sink.WriteStdout("visible");

            stdout.ToString().ShouldContain("visible");
            stdout.ToString().ShouldContain("##squid[setVariable name='X' value='1']");
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
