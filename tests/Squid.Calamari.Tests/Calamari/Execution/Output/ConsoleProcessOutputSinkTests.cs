using Squid.Calamari.Execution.Output;

namespace Squid.Calamari.Tests.Calamari.Execution.Output;

[Collection("Console IO")]
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
    public void ScriptOutputProcessor_ServiceMessage_IsBothCollectedAndForwarded()
    {
        // Both halves matter and they serve different consumers: the collector feeds THIS
        // process's own conventions (PostDeploy reading a value the main script computed),
        // while stdout is what carries the variable back to the server. Losing either one is
        // a silent failure, so pin them together at the level that wires them.
        var processor = new global::Squid.Calamari.Execution.ScriptOutputProcessor();
        using var stdout = new StringWriter();
        var originalOut = Console.Out;

        try
        {
            Console.SetOut(stdout);

            processor.ProcessLine("##squid[setVariable name='Url' value='https://web.test']");
            processor.ProcessLine("ordinary log line");

            processor.OutputVariables.ShouldContain(v => v.Name == "Url" && v.Value == "https://web.test",
                "the collector feeds this process's own conventions");
            stdout.ToString().ShouldContain("##squid[setVariable name='Url' value='https://web.test']");
            stdout.ToString().ShouldContain("ordinary log line");
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
