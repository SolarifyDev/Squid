using Squid.Calamari.Execution.Output;

namespace Squid.Calamari.Tests.Calamari.Execution.Output;

public class CompositeProcessOutputSinkTests
{
    [Fact]
    public void WriteStdout_And_WriteStderr_FanOutToAllSinks()
    {
        var sinkA = new RecordingSink();
        var sinkB = new RecordingSink();
        var composite = new CompositeProcessOutputSink(sinkA, sinkB);

        composite.WriteStdout("hello");
        composite.WriteStderr("oops");

        sinkA.Stdout.ShouldBe(["hello"]);
        sinkA.Stderr.ShouldBe(["oops"]);
        sinkB.Stdout.ShouldBe(["hello"]);
        sinkB.Stderr.ShouldBe(["oops"]);
    }

    private sealed class RecordingSink : IProcessOutputSink
    {
        public List<string> Stdout { get; } = new();
        public List<string> Stderr { get; } = new();

        public void WriteStdout(string line) => Stdout.Add(line);

        public void WriteStderr(string line) => Stderr.Add(line);
    }
}
