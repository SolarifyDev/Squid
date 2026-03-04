using Squid.Calamari.Execution.Output;

namespace Squid.Calamari.Tests.Calamari.Execution.Output;

public class SquidServiceMessageOutputSinkTests
{
    [Fact]
    public void WriteStdout_ServiceMessage_CollectsOutputVariable()
    {
        var collector = new OutputVariableCollectorSink();
        var sink = new SquidServiceMessageOutputSink(collector);

        sink.WriteStdout("##squid[setVariable name='Result' value='OK' sensitive='True']");

        collector.OutputVariables.Count.ShouldBe(1);
        collector.OutputVariables[0].Name.ShouldBe("Result");
        collector.OutputVariables[0].Value.ShouldBe("OK");
        collector.OutputVariables[0].IsSensitive.ShouldBeTrue();
    }

    [Fact]
    public void WriteStdout_NonServiceMessage_Ignored()
    {
        var collector = new OutputVariableCollectorSink();
        var sink = new SquidServiceMessageOutputSink(collector);

        sink.WriteStdout("normal log line");

        collector.OutputVariables.ShouldBeEmpty();
    }

    [Fact]
    public void WriteStderr_DoesNotParseServiceMessages()
    {
        var collector = new OutputVariableCollectorSink();
        var sink = new SquidServiceMessageOutputSink(collector);

        sink.WriteStderr("##squid[setVariable name='X' value='1']");

        collector.OutputVariables.ShouldBeEmpty();
    }
}
