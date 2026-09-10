using Squid.Calamari.Execution;

namespace Squid.Calamari.Tests.Calamari.Execution;

[Collection("Process Globals")]
public class ScriptOutputProcessorTests
{
    [Fact]
    public void ProcessLine_ServiceMessage_IsCollectedAndAlsoForwardedToStdout()
    {
        var processor = new ScriptOutputProcessor();
        using var stdout = new StringWriter();
        var originalOut = Console.Out;

        try
        {
            Console.SetOut(stdout);

            processor.ProcessLine("##squid[setVariable name='BuildId' value='42']");

            processor.OutputVariables.Count.ShouldBe(1);
            processor.OutputVariables[0].Name.ShouldBe("BuildId");
            processor.OutputVariables[0].Value.ShouldBe("42");
            // Forwarded, not suppressed: stdout is how the variable reaches the SERVER
            // (Tentacle -> log lines -> ExecuteStepsPhase.CaptureOutputVariables). The collector
            // above serves only this process's own conventions.
            stdout.ToString().ShouldContain("##squid[setVariable name='BuildId' value='42']");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void ProcessLine_RegularStdoutAndStderr_AreForwarded()
    {
        var processor = new ScriptOutputProcessor();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalErr = Console.Error;

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);

            processor.ProcessLine("hello");
            processor.ProcessLine("fail", isError: true);

            stdout.ToString().ShouldContain("hello");
            stderr.ToString().ShouldContain("fail");
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }
}
