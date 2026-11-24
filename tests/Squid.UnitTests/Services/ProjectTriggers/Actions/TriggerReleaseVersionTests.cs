using Squid.Core.Services.ProjectTriggers.Actions;

namespace Squid.UnitTests.Services.ProjectTriggers.Actions;

public class TriggerReleaseVersionTests
{
    [Fact]
    public void DefaultPolicy_StartsAtOneAndIncrementsHighestNumericPatch()
    {
        TriggerReleaseVersion.Next([]).ShouldBe("1.0.0");
        TriggerReleaseVersion.Next(["1.9.99", "1.10.12", "1.10.9"]).ShouldBe("1.10.13");
    }

    [Theory]
    [InlineData("1.0.0-alpha.1")]
    [InlineData("1.2")]
    [InlineData("2026.1.0.4")]
    [InlineData("1.0.2147483647")]
    public void UnsupportedHistory_ExplainsWhyAutomaticAllocationStopped(string version)
    {
        Should.Throw<TriggerActionRejectedException>(() => TriggerReleaseVersion.Next([version])).Message.ShouldContain("major.minor.patch");
    }

    [Theory]
    [InlineData("{\"deploymentSettings\":{\"hasVersioningStrategy\":true}}")]
    [InlineData("{\"VersioningStrategy\":{\"DonorPackage\":\"package\"}}")]
    [InlineData("{\"DeploymentSettings\":{\"VersioningStrategy\":{\"Template\":\"#{custom}\"}}}")]
    [InlineData("{bad-json")]
    public void UnsupportedOrLostProjectStrategy_ExplainsWhyNoVersionWasAllocated(string projectJson)
    {
        Should.Throw<TriggerActionRejectedException>(() => TriggerReleaseVersion.EnsureDefaultPolicy(projectJson)).Message.ShouldContain("versioning");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"deploymentSettings\":{\"hasVersioningStrategy\":false}}")]
    public void ProjectsWithDefaultSettings_UseTheDefaultPolicy(string projectJson)
    {
        Should.NotThrow(() => TriggerReleaseVersion.EnsureDefaultPolicy(projectJson));
    }
}
