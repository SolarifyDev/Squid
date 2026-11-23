using System.Linq;
using Microsoft.Extensions.Configuration;
using Squid.Core.Services.Deployments.ServerTask.Lease;
using Squid.Core.Settings.ServerTask;

namespace Squid.UnitTests.Services.Deployments.ServerTask.Lease;

public class ServerTaskLeaseOptionsTests
{
    private static ServerTaskSetting Bind(params (string Key, string Value)[] pairs)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value))).Build();

        return new ServerTaskSetting(configuration);
    }

    [Fact]
    public void NoConfiguration_YieldsTwentySecondRenewal_FiveMinuteStaleness()
    {
        var options = ServerTaskLeaseOptions.From(Bind());

        options.RenewInterval.ShouldBe(TimeSpan.FromSeconds(20));
        options.Stale.ShouldBe(TimeSpan.FromSeconds(300));
    }

    // Renaming a key silently drops an operator's override; pin the section path and both key names.
    [Fact]
    public void ConfigurationKeys_ServerTaskLease_AreBound()
    {
        var setting = Bind(("ServerTask:Lease:RenewIntervalSeconds", "30"), ("ServerTask:Lease:StaleSeconds", "600"));

        setting.Lease.RenewIntervalSeconds.ShouldBe(30);
        setting.Lease.StaleSeconds.ShouldBe(600);
        ServerTaskLeaseOptions.From(setting).RenewInterval.ShouldBe(TimeSpan.FromSeconds(30));
        ServerTaskLeaseOptions.From(setting).Stale.ShouldBe(TimeSpan.FromSeconds(600));
    }

    [Fact]
    public void Defaults_OfTheBoundSetting_ArePinned()
    {
        var setting = Bind();

        setting.Lease.RenewIntervalSeconds.ShouldBe(20);
        setting.Lease.StaleSeconds.ShouldBe(300);
        setting.Lease.MaxAutoResumes.ShouldBe(3);
    }

    // Renaming the key silently drops an operator's override of how often a crashing deployment is resumed on its own.
    [Fact]
    public void ConfigurationKey_MaxAutoResumes_IsBound()
    {
        var setting = Bind(("ServerTask:Lease:MaxAutoResumes", "7"));

        setting.Lease.MaxAutoResumes.ShouldBe(7);
        ServerTaskLeaseOptions.From(setting).MaxAutoResumes.ShouldBe(7);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(-4, 3)]
    [InlineData(1, 1)]
    [InlineData(10, 10)]
    public void MaxAutoResumes_FallsBackToTheDefault_WhenNotPositive(int configured, int expected)
    {
        var setting = new ServerTaskSetting { Lease = new ServerTaskLeaseSettings { MaxAutoResumes = configured } };

        ServerTaskLeaseOptions.From(setting).MaxAutoResumes.ShouldBe(expected);
    }

    [Theory]
    [InlineData(-5, 0, 20, 300)]
    [InlineData(0, -1, 20, 300)]
    [InlineData(1, 300, 5, 300)]      // interval floors at 5s so renewal cannot hammer the database
    [InlineData(60, 100, 60, 180)]    // stale never drops below three intervals, or one slow renewal would look like a death
    [InlineData(30, 600, 30, 600)]
    public void Values_FallBackOrClamp(int interval, int stale, int expectedInterval, int expectedStale)
    {
        var setting = new ServerTaskSetting { Lease = new ServerTaskLeaseSettings { RenewIntervalSeconds = interval, StaleSeconds = stale } };

        var options = ServerTaskLeaseOptions.From(setting);

        options.RenewInterval.ShouldBe(TimeSpan.FromSeconds(expectedInterval));
        options.Stale.ShouldBe(TimeSpan.FromSeconds(expectedStale));
    }

    [Theory]
    [InlineData(20, 300, 260)]
    [InlineData(5, 15, 5)]
    [InlineData(10, 30, 10)]
    public void SelfFence_IsTwoIntervalsShortOfTheStaleThreshold_SoARunStopsBeforeTheReaperMayRecoverIt(int interval, int stale, int expectedFence)
    {
        var options = ServerTaskLeaseOptions.From(new ServerTaskSetting { Lease = new ServerTaskLeaseSettings { RenewIntervalSeconds = interval, StaleSeconds = stale } });

        options.SelfFence.ShouldBe(TimeSpan.FromSeconds(expectedFence));
        options.SelfFence.ShouldBeLessThan(options.Stale);
    }

    [Fact]
    public void NewRun_IsDistinctPerRun_ExtendsTheProcessIdentity_AndFitsTheColumn()
    {
        var first = ServerTaskLeaseOwner.NewRun();
        var second = ServerTaskLeaseOwner.NewRun();

        first.ShouldNotBe(second);
        first.ShouldStartWith(ServerTaskLeaseOwner.Current + "/");
        first.Length.ShouldBeLessThanOrEqualTo(200);
    }

    [Fact]
    public void LeaseOwner_IsStablePerProcess_AndFitsTheColumn()
    {
        ServerTaskLeaseOwner.Current.ShouldBe(ServerTaskLeaseOwner.Current);
        ServerTaskLeaseOwner.Current.Length.ShouldBeLessThanOrEqualTo(200);
        ServerTaskLeaseOwner.Current.ShouldContain($":{Environment.ProcessId}:");
    }
}
