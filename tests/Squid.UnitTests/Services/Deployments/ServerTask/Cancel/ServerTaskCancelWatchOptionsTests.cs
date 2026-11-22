using System.Linq;
using Microsoft.Extensions.Configuration;
using Squid.Core.Services.Deployments.ServerTask.Cancel;
using Squid.Core.Settings.ServerTask;

namespace Squid.UnitTests.Services.Deployments.ServerTask.Cancel;

public class ServerTaskCancelWatchOptionsTests
{
    private static ServerTaskSetting Bind(params (string Key, string Value)[] pairs)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value))).Build();

        return new ServerTaskSetting(configuration);
    }

    [Fact]
    public void NoConfiguration_YieldsThreeSecondPoll()
    {
        var setting = Bind();

        setting.CancelWatch.PollIntervalSeconds.ShouldBe(3);
        ServerTaskCancelWatchOptions.From(setting).PollInterval.ShouldBe(TimeSpan.FromSeconds(3));
    }

    // Renaming a key silently drops an operator's override; pin the section path and key name.
    [Fact]
    public void ConfigurationKey_ServerTaskCancelWatch_IsBound()
    {
        var setting = Bind(("ServerTask:CancelWatch:PollIntervalSeconds", "10"));

        setting.CancelWatch.PollIntervalSeconds.ShouldBe(10);
        ServerTaskCancelWatchOptions.From(setting).PollInterval.ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void NonPositive_FallsBackToDefault(string raw)
    {
        ServerTaskCancelWatchOptions.From(Bind(("ServerTask:CancelWatch:PollIntervalSeconds", raw))).PollInterval.ShouldBe(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void One_IsTheMinimumAccepted()
    {
        ServerTaskCancelWatchOptions.From(Bind(("ServerTask:CancelWatch:PollIntervalSeconds", "1"))).PollInterval.ShouldBe(TimeSpan.FromSeconds(1));
    }
}
