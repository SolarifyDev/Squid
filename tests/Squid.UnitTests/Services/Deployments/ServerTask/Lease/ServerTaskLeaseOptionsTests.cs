using Squid.Core.Services.Deployments.ServerTask.Lease;

namespace Squid.UnitTests.Services.Deployments.ServerTask.Lease;

public class ServerTaskLeaseOptionsTests
{
    // Renaming one of these silently drops an operator's override and turns it into a production surprise.
    [Fact]
    public void EnvVarNames_ArePinned()
    {
        ServerTaskLeaseOptions.RenewIntervalSecondsEnvVar.ShouldBe("SQUID_SERVER_TASK_LEASE_RENEW_INTERVAL_SECONDS");
        ServerTaskLeaseOptions.StaleSecondsEnvVar.ShouldBe("SQUID_SERVER_TASK_LEASE_STALE_SECONDS");
        ServerTaskLeaseOptions.ReaperEnabledEnvVar.ShouldBe("SQUID_SERVER_TASK_REAPER_ENABLED");
    }

    [Fact]
    public void Defaults_AreTwentySecondRenewal_FiveMinuteStaleness_ReaperOn()
    {
        var options = ServerTaskLeaseOptions.From(_ => null);

        options.RenewInterval.ShouldBe(TimeSpan.FromSeconds(20));
        options.Stale.ShouldBe(TimeSpan.FromSeconds(300));
        options.ReaperEnabled.ShouldBeTrue();
    }

    [Theory]
    [InlineData("30", "600", 30, 600)]
    [InlineData("abc", "xyz", 20, 300)]
    [InlineData("", "", 20, 300)]
    [InlineData("-5", "0", 20, 300)]
    [InlineData("1", "300", 5, 300)]      // interval floors at 5s so renewal cannot hammer the database
    [InlineData("60", "100", 60, 180)]    // stale never drops below three intervals, or one slow renewal would look like a death
    public void Parsing_FallsBackOrClamps(string interval, string stale, int expectedInterval, int expectedStale)
    {
        var options = ServerTaskLeaseOptions.From(name => name == ServerTaskLeaseOptions.RenewIntervalSecondsEnvVar ? interval : name == ServerTaskLeaseOptions.StaleSecondsEnvVar ? stale : null);

        options.RenewInterval.ShouldBe(TimeSpan.FromSeconds(expectedInterval));
        options.Stale.ShouldBe(TimeSpan.FromSeconds(expectedStale));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("true", true)]
    [InlineData("garbage", true)]   // a typo must not switch recovery off
    [InlineData("false", false)]
    [InlineData(" FALSE ", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("Off", false)]
    public void ReaperEnabled_OnlyAnExplicitFalseyTokenSwitchesItOff(string raw, bool expected)
    {
        ServerTaskLeaseOptions.From(name => name == ServerTaskLeaseOptions.ReaperEnabledEnvVar ? raw : null).ReaperEnabled.ShouldBe(expected);
    }

    [Fact]
    public void LeaseOwner_IsStablePerProcess_AndFitsTheColumn()
    {
        ServerTaskLeaseOwner.Current.ShouldBe(ServerTaskLeaseOwner.Current);
        ServerTaskLeaseOwner.Current.Length.ShouldBeLessThanOrEqualTo(200);
        ServerTaskLeaseOwner.Current.ShouldContain($":{Environment.ProcessId}:");
    }
}
