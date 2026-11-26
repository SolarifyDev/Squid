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

    // ── The retry wait: exponential, capped per retry and in total ──────

    [Fact]
    public void NoSetting_CapsEachWaitAtFiveSeconds_AndAllWaitsAtFifteen()
    {
        var options = ServerTaskTerminalWriteOptions.From(null);

        options.MaxDelay.ShouldBe(TimeSpan.FromSeconds(5));
        options.MaxTotalDelay.ShouldBe(TimeSpan.FromSeconds(15));
    }

    // The caps never bind on the default schedule: 200 ms then 600 ms, then the attempts are used up — exactly as before.
    [Fact]
    public void DefaultSchedule_IsUnchanged()
    {
        var options = ServerTaskTerminalWriteOptions.From(null);

        options.RetryDelay(1, TimeSpan.Zero).ShouldBe(TimeSpan.FromMilliseconds(200));
        options.RetryDelay(2, TimeSpan.FromMilliseconds(200)).ShouldBe(TimeSpan.FromMilliseconds(600));
        options.RetryDelay(3, TimeSpan.FromMilliseconds(800)).ShouldBeNull();
    }

    [Theory]
    [InlineData(5, 5000)]       // 200 × 3^4 = 16 200 ms → capped
    [InlineData(16, 5000)]      // 200 × 3^15 ≈ 2.9e9 ms: past int.MaxValue, which made Task.Delay throw ArgumentOutOfRangeException
    [InlineData(2000, 5000)]    // 3^1999 is Infinity as a double
    public void EachWait_IsCappedAtTheMaximum_ForAnyAttempt(int attempt, int expectedMilliseconds)
    {
        var options = ServerTaskTerminalWriteOptions.From(new ServerTaskSetting { TerminalWrite = new ServerTaskTerminalWriteSettings { MaxAttempts = int.MaxValue } });

        options.RetryDelay(attempt, TimeSpan.Zero).ShouldBe(TimeSpan.FromMilliseconds(expectedMilliseconds));
    }

    [Theory]
    [InlineData(0, 5000)]          // the full wait fits
    [InlineData(10000, 5000)]      // exactly fills the budget
    [InlineData(14000, 1000)]      // only the rest of the budget is waited
    [InlineData(15000, null)]      // budget used up: no more retries, the failure surfaces
    [InlineData(20000, null)]
    public void AllWaits_AreCappedByTheTotalBudget(int waitedMilliseconds, int? expectedMilliseconds)
    {
        var options = ServerTaskTerminalWriteOptions.From(new ServerTaskSetting { TerminalWrite = new ServerTaskTerminalWriteSettings { MaxAttempts = int.MaxValue } });

        options.RetryDelay(10, TimeSpan.FromMilliseconds(waitedMilliseconds)).ShouldBe(expectedMilliseconds == null ? null : TimeSpan.FromMilliseconds(expectedMilliseconds.Value));
    }

    [Fact]
    public void ConfigurationKeys_ForTheCaps_AreBound()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["ServerTask:TerminalWrite:MaxRetryDelayMilliseconds"] = "700", ["ServerTask:TerminalWrite:MaxTotalRetryDelayMilliseconds"] = "900" }).Build();

        var options = ServerTaskTerminalWriteOptions.From(new ServerTaskSetting(configuration));

        options.MaxDelay.ShouldBe(TimeSpan.FromMilliseconds(700));
        options.MaxTotalDelay.ShouldBe(TimeSpan.FromMilliseconds(900));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, -5)]
    public void NonPositiveCaps_FallBackToTheDefaults(int maxDelay, int maxTotal)
    {
        var options = ServerTaskTerminalWriteOptions.From(new ServerTaskSetting { TerminalWrite = new ServerTaskTerminalWriteSettings { MaxRetryDelayMilliseconds = maxDelay, MaxTotalRetryDelayMilliseconds = maxTotal } });

        options.MaxDelay.ShouldBe(TimeSpan.FromSeconds(5));
        options.MaxTotalDelay.ShouldBe(TimeSpan.FromSeconds(15));
    }
}
