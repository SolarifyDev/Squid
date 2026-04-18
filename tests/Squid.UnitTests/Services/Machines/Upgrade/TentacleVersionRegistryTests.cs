using Squid.Core.Services.Machines.Upgrade;
using Squid.Message.Enums;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// Coverage for the live Tentacle-version source-of-truth. Single
/// responsibility: "given a CommunicationStyle, what is the latest
/// published version". URL building / artefact delivery deliberately lives
/// elsewhere (per-strategy) — see the SOLID note on
/// <see cref="ITentacleVersionRegistry"/>.
///
/// <para>Live Docker Hub queries are NOT exercised here — those need the
/// network and would be flaky in CI; integration tests cover the live
/// path. The HTTP boundary is short-circuited by env-var overrides for
/// every test in this file, proving the override path runs before any IO.</para>
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

        // Pass null HTTP factory — proves the override short-circuits BEFORE
        // any network IO (otherwise the registry would NPE on the HTTP path).
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
    public async Task GetLatestVersionAsync_UnknownStyle_ReturnsEmptyWithoutCrash()
    {
        // SSH targets (and any future style) shouldn't error the upgrade
        // pipeline; the orchestrator turns empty into a NotSupported
        // response with style name in the detail.
        var registry = new TentacleVersionRegistry(httpClientFactory: null);

        var version = await registry.GetLatestVersionAsync("Ssh", CancellationToken.None);

        version.ShouldBeEmpty();
    }
}
