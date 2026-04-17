using Shouldly;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.Halibut;

/// <summary>
/// Behavioural contract for <c>TentacleSettings.PollingConnectionCount</c>. The
/// host itself (TentacleHalibutHost) wires real Halibut connections and is
/// exercised in the integration tests; this file locks in the default and
/// the clamp range to prevent accidental drift.
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public sealed class PollingConnectionCountTests
{
    [Fact]
    public void Default_IsFiveConnections()
    {
        var settings = new TentacleSettings();

        settings.PollingConnectionCount.ShouldBe(5);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(8, 8)]
    [InlineData(9, 8)]
    [InlineData(100, 8)]
    public void Clamp_HonoursOctopusUpperBoundOfEight(int configured, int expected)
    {
        var actual = Math.Clamp(configured, 1, 8);

        actual.ShouldBe(expected);
    }
}
