using System.Linq;
using Microsoft.Extensions.Configuration;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Settings.ServerTask;

namespace Squid.UnitTests.Services.Deployments.ServerTask;

public class ServerTaskTerminalWriteOptionsTests
{
    [Fact]
    public void NoSetting_YieldsThreeAttempts_TwoHundredMillisecondsFirstDelay()
    {
        var options = ServerTaskTerminalWriteOptions.From(null);

        options.MaxAttempts.ShouldBe(3);
        options.InitialDelay.ShouldBe(TimeSpan.FromMilliseconds(200));
    }

    // Renaming a key silently drops an operator's override; pin the section path and both key names.
    [Fact]
    public void ConfigurationKeys_ServerTaskTerminalWrite_AreBound()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["ServerTask:TerminalWrite:MaxAttempts"] = "5", ["ServerTask:TerminalWrite:RetryDelayMilliseconds"] = "50" }).Build();

        var options = ServerTaskTerminalWriteOptions.From(new ServerTaskSetting(configuration));

        options.MaxAttempts.ShouldBe(5);
        options.InitialDelay.ShouldBe(TimeSpan.FromMilliseconds(50));
    }

    [Theory]
    [InlineData(0, -1, 3, 200)]
    [InlineData(-4, 0, 3, 0)]
    public void InvalidValues_FallBackToTheDefaults(int attempts, int delayMilliseconds, int expectedAttempts, int expectedDelayMilliseconds)
    {
        var setting = new ServerTaskSetting { TerminalWrite = new ServerTaskTerminalWriteSettings { MaxAttempts = attempts, RetryDelayMilliseconds = delayMilliseconds } };

        var options = ServerTaskTerminalWriteOptions.From(setting);

        options.MaxAttempts.ShouldBe(expectedAttempts);
        options.InitialDelay.ShouldBe(TimeSpan.FromMilliseconds(expectedDelayMilliseconds));
    }
}
