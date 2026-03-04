using Squid.Calamari.Execution;

namespace Squid.Calamari.Tests.Calamari.Execution;

[Collection("Console IO")]
public class ScriptOutputProcessorTests
{
    [Fact]
    public void ProcessLine_ServiceMessage_IsCollectedAndNotPrinted()
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
            stdout.ToString().ShouldNotContain("##squid[setVariable");
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
