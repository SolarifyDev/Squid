using System.IO;
using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Commands;

namespace Squid.Tentacle.Tests.Commands;

public sealed class VersionCommandTests
{
    [Fact]
    public async Task Execute_WritesVersionToStdoutAndExitsZero()
    {
        // The whole point of this command: return cleanly, not fall through
        // to RunCommand which would start the agent. Regression scenario:
        // someone adds arg parsing and forgets the no-arg path. Pin by
        // ensuring a no-arg invocation produces ONE line + exits 0 in
        // under a second (way faster than RunCommand's agent bootstrap).
        var originalOut = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);

        try
        {
            var cmd = new VersionCommand();
            var cfg = new ConfigurationBuilder().Build();

            var task = cmd.ExecuteAsync(Array.Empty<string>(), cfg, CancellationToken.None);

            (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(1)))).ShouldBe(task,
                "VersionCommand must complete in under 1 second — anything longer means it's doing something wrong (like starting the agent)");

            var exit = await task;
            exit.ShouldBe(0);

            var output = captured.ToString().Trim();
            output.ShouldNotBeEmpty();
            output.Split('\n').Length.ShouldBe(1,
                "version must be a single-line output for easy parsing by the upgrade script");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void Metadata_ExposesNameAndDescription()
    {
        var cmd = new VersionCommand();
        cmd.Name.ShouldBe("version",
            "must exactly be 'version' — upgrade script invokes `squid-tentacle version` (subcommand form) to avoid the `--version` fallback-to-RunCommand CLI gotcha");
        cmd.Description.ShouldNotBeNullOrWhiteSpace();
    }
}
