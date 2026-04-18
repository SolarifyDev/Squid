using Squid.Core.Services.Machines.Upgrade;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// Coverage for the embedded-resource → version-string contract that is the
/// single source of truth for "which Tentacle version does this server want
/// every agent on". A regression here means the entire upgrade flow silently
/// degrades to "no recommendation" and operators must specify TargetVersion
/// for every upgrade — the symptom we explicitly want to avoid.
/// </summary>
public sealed class BundledTentacleVersionProviderTests
{
    private readonly BundledTentacleVersionProvider _provider = new();

    [Fact]
    public void GetBundledVersion_EmbeddedResourceLoads_ReturnsTrimmedNonEmpty()
    {
        var version = _provider.GetBundledVersion();

        // Resource exists in the assembly (csproj wires it via EmbeddedResource
        // Include="Resources\Upgrade\*"); failing here means the resource was
        // dropped from the build output — broken contract for every consumer.
        version.ShouldNotBeNullOrEmpty();
        version.ShouldNotEndWith("\n");
        version.ShouldNotEndWith("\r");

        // Sanity: must parse as semver — versions like "abc" would slip
        // through into the upgrade URL and produce 404s when the agent tries
        // to download.
        Version.TryParse(version, out _).ShouldBeTrue($"bundled version '{version}' must be a parseable Version");
    }

    [Theory]
    [InlineData("1.4.0", "linux-x64", "https://github.com/SolarifyDev/Squid/releases/download/1.4.0/squid-tentacle-1.4.0-linux-x64.tar.gz")]
    [InlineData("1.4.0", "linux-arm64", "https://github.com/SolarifyDev/Squid/releases/download/1.4.0/squid-tentacle-1.4.0-linux-arm64.tar.gz")]
    [InlineData("2.0.0-beta.1", "linux-x64", "https://github.com/SolarifyDev/Squid/releases/download/2.0.0-beta.1/squid-tentacle-2.0.0-beta.1-linux-x64.tar.gz")]
    public void GetDownloadUrl_BuildsPublishedReleaseAssetUrl(string version, string rid, string expected)
    {
        _provider.GetDownloadUrl(version, rid).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null, "linux-x64")]
    [InlineData("", "linux-x64")]
    [InlineData("  ", "linux-x64")]
    [InlineData("1.4.0", null)]
    [InlineData("1.4.0", "")]
    public void GetDownloadUrl_RejectsBlankInputs(string version, string rid)
    {
        Should.Throw<ArgumentException>(() => _provider.GetDownloadUrl(version, rid));
    }
}
