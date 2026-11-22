using Squid.Core.Services.Deployments.ServerTask.Cancel;

namespace Squid.UnitTests.Services.Deployments.ServerTask.Cancel;

public class ServerTaskCancelWatchOptionsTests
{
    [Fact]
    public void EnvVarName_IsPinned()
    {
        // Renaming this constant silently drops every operator's override.
        ServerTaskCancelWatchOptions.PollIntervalSecondsEnvVar.ShouldBe("SQUID_SERVER_TASK_CANCEL_POLL_INTERVAL_SECONDS");
    }

    [Theory]
    [InlineData(null, 3)]
    [InlineData("", 3)]
    [InlineData("abc", 3)]
    [InlineData("0", 3)]
    [InlineData("-5", 3)]
    [InlineData("1", 1)]
    [InlineData("10", 10)]
    public void PollInterval_ParsesSecondsAndFallsBackToDefault(string raw, int expectedSeconds)
    {
        var options = ServerTaskCancelWatchOptions.From(name => name == ServerTaskCancelWatchOptions.PollIntervalSecondsEnvVar ? raw : null);

        options.PollInterval.ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
    }
}
