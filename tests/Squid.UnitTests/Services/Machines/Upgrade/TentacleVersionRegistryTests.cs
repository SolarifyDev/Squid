using Squid.Core.Services.Machines.Upgrade;
using Squid.Message.Enums;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// Coverage for the live Tentacle-version source-of-truth. Validates the
/// resolution chain (env override → cache → live query → stale fallback →
/// empty), the per-style routing, and the URL construction the upgrade
/// strategy depends on. Live Docker Hub queries are NOT exercised here —
/// those need the network and would be flaky; integration tests cover that.
/// </summary>
public sealed class TentacleVersionRegistryTests : IDisposable
{
    private readonly string _previousLinuxOverride;
    private readonly string _previousK8sOverride;

    public TentacleVersionRegistryTests()
    {
        // Snapshot any pre-existing values so the test process doesn't pollute
        // sibling tests that may share the env. Restored in Dispose().
        _previousLinuxOverride = Environment.GetEnvironmentVariable(TentacleVersionRegistry.LinuxOverrideEnvVar);
        _previousK8sOverride = Environment.GetEnvironmentVariable(TentacleVersionRegistry.K8sOverrideEnvVar);
        Environment.SetEnvironmentVariable(TentacleVersionRegistry.LinuxOverrideEnvVar, null);
        Environment.SetEnvironmentVariable(TentacleVersionRegistry.K8sOverrideEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TentacleVersionRegistry.LinuxOverrideEnvVar, _previousLinuxOverride);
        Environment.SetEnvironmentVariable(TentacleVersionRegistry.K8sOverrideEnvVar, _previousK8sOverride);
    }

    [Fact]
    public void OverrideEnvVar_LinuxConstantNamePinned()
    {
        // Renaming this constant breaks every air-gapped / canary deployment
        // that pinned a Linux tentacle version via env. Hard-pin in test.
        TentacleVersionRegistry.LinuxOverrideEnvVar.ShouldBe("SQUID_TARGET_LINUX_TENTACLE_VERSION");
    }

    [Fact]
    public void OverrideEnvVar_K8sConstantNamePinned()
    {
        TentacleVersionRegistry.K8sOverrideEnvVar.ShouldBe("SQUID_TARGET_K8S_AGENT_VERSION");
    }

    [Theory]
    [InlineData(nameof(CommunicationStyle.TentaclePolling), "1.4.2")]
    [InlineData(nameof(CommunicationStyle.TentacleListening), "1.4.2")]
    public async Task GetLatestVersionAsync_LinuxStyleWithEnvOverride_ReturnsOverrideValueWithoutHttp(string style, string expected)
    {
        Environment.SetEnvironmentVariable(TentacleVersionRegistry.LinuxOverrideEnvVar, expected);

        // We intentionally pass a registry whose HTTP factory would NPE if
        // touched — proves the override short-circuits before any network IO.
        var registry = new TentacleVersionRegistry(httpClientFactory: null);

        var version = await registry.GetLatestVersionAsync(style, CancellationToken.None);

        version.ShouldBe(expected);
    }

    [Fact]
    public async Task GetLatestVersionAsync_K8sStyleWithEnvOverride_ReturnsOverrideValueWithoutHttp()
    {
        Environment.SetEnvironmentVariable(TentacleVersionRegistry.K8sOverrideEnvVar, "2.0.0-canary.1");

        var registry = new TentacleVersionRegistry(httpClientFactory: null);

        var version = await registry.GetLatestVersionAsync(nameof(CommunicationStyle.KubernetesAgent), CancellationToken.None);

        version.ShouldBe("2.0.0-canary.1");
    }

    [Fact]
    public async Task GetLatestVersionAsync_OverrideTrimmed_StripsWhitespace()
    {
        Environment.SetEnvironmentVariable(TentacleVersionRegistry.LinuxOverrideEnvVar, "  1.4.2  ");

        var registry = new TentacleVersionRegistry(httpClientFactory: null);

        var version = await registry.GetLatestVersionAsync(nameof(CommunicationStyle.TentaclePolling), CancellationToken.None);

        version.ShouldBe("1.4.2");
    }

    [Fact]
    public async Task GetLatestVersionAsync_UnknownStyle_ReturnsEmpty()
    {
        var registry = new TentacleVersionRegistry(httpClientFactory: null);

        var version = await registry.GetLatestVersionAsync("Ssh", CancellationToken.None);

        version.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("1.4.0", "linux-x64", "https://github.com/SolarifyDev/Squid/releases/download/1.4.0/squid-tentacle-1.4.0-linux-x64.tar.gz")]
    [InlineData("1.4.0", "linux-arm64", "https://github.com/SolarifyDev/Squid/releases/download/1.4.0/squid-tentacle-1.4.0-linux-arm64.tar.gz")]
    [InlineData("2.0.0-beta.1", "linux-x64", "https://github.com/SolarifyDev/Squid/releases/download/2.0.0-beta.1/squid-tentacle-2.0.0-beta.1-linux-x64.tar.gz")]
    public void GetLinuxDownloadUrl_BuildsPublishedReleaseAssetUrl(string version, string rid, string expected)
    {
        var registry = new TentacleVersionRegistry(httpClientFactory: null);

        registry.GetLinuxDownloadUrl(version, rid).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null, "linux-x64")]
    [InlineData("", "linux-x64")]
    [InlineData("  ", "linux-x64")]
    [InlineData("1.4.0", null)]
    [InlineData("1.4.0", "")]
    public void GetLinuxDownloadUrl_RejectsBlankInputs(string version, string rid)
    {
        var registry = new TentacleVersionRegistry(httpClientFactory: null);

        Should.Throw<ArgumentException>(() => registry.GetLinuxDownloadUrl(version, rid));
    }
}
