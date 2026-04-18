using Squid.Core.Services.Machines.Upgrade;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// Coverage for the parts of <see cref="LinuxTentacleUpgradeStrategy"/> that
/// don't need a live Halibut connection — chiefly the per-platform delivery
/// URL pattern. Halibut dispatch + script observation is exercised end-to-end
/// in the integration suite (Phase 2 work).
/// </summary>
public sealed class LinuxTentacleUpgradeStrategyTests : IDisposable
{
    private readonly string _previousBaseUrlOverride;

    public LinuxTentacleUpgradeStrategyTests()
    {
        _previousBaseUrlOverride = Environment.GetEnvironmentVariable(LinuxTentacleUpgradeStrategy.DownloadBaseUrlEnvVar);

        Environment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.DownloadBaseUrlEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.DownloadBaseUrlEnvVar, _previousBaseUrlOverride);
    }

    [Fact]
    public void DownloadBaseUrlEnvVar_ConstantNamePinned()
    {
        // Renaming this constant breaks every air-gapped operator who pinned
        // a private mirror via env. Hard-pin in test.
        LinuxTentacleUpgradeStrategy.DownloadBaseUrlEnvVar.ShouldBe("SQUID_TARGET_LINUX_TENTACLE_DOWNLOAD_BASE_URL");
    }

    [Theory]
    [InlineData("1.4.0", "linux-x64", "https://github.com/SolarifyDev/Squid/releases/download/1.4.0/squid-tentacle-1.4.0-linux-x64.tar.gz")]
    [InlineData("1.4.0", "linux-arm64", "https://github.com/SolarifyDev/Squid/releases/download/1.4.0/squid-tentacle-1.4.0-linux-arm64.tar.gz")]
    [InlineData("2.0.0-beta.1", "linux-x64", "https://github.com/SolarifyDev/Squid/releases/download/2.0.0-beta.1/squid-tentacle-2.0.0-beta.1-linux-x64.tar.gz")]
    public void BuildDownloadUrl_DefaultsToGitHubReleasesPath(string version, string rid, string expected)
    {
        LinuxTentacleUpgradeStrategy.BuildDownloadUrl(version, rid).ShouldBe(expected);
    }

    [Theory]
    [InlineData("https://mirror.acme.internal/squid", "1.4.0", "linux-x64", "https://mirror.acme.internal/squid/1.4.0/squid-tentacle-1.4.0-linux-x64.tar.gz")]
    [InlineData("https://s3.example.com/squid-mirror/", "1.4.0", "linux-arm64", "https://s3.example.com/squid-mirror/1.4.0/squid-tentacle-1.4.0-linux-arm64.tar.gz")]
    public void BuildDownloadUrl_EnvOverride_RetargetsToOperatorMirror(string baseUrl, string version, string rid, string expected)
    {
        Environment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.DownloadBaseUrlEnvVar, baseUrl);

        LinuxTentacleUpgradeStrategy.BuildDownloadUrl(version, rid).ShouldBe(expected);
    }

    [Fact]
    public void ResolveDownloadBaseUrl_StripsTrailingSlash_ToPreventDoubleSlashInUrl()
    {
        Environment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.DownloadBaseUrlEnvVar, "https://mirror.acme.internal/path/");

        LinuxTentacleUpgradeStrategy.ResolveDownloadBaseUrl().ShouldBe("https://mirror.acme.internal/path");
    }

    [Fact]
    public void ResolveDownloadBaseUrl_BlankOverride_FallsBackToDefault()
    {
        Environment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.DownloadBaseUrlEnvVar, "   ");

        LinuxTentacleUpgradeStrategy.ResolveDownloadBaseUrl().ShouldBe("https://github.com/SolarifyDev/Squid/releases/download");
    }

    [Theory]
    [InlineData("TentaclePolling", true)]
    [InlineData("TentacleListening", true)]
    [InlineData("KubernetesAgent", false)]
    [InlineData("KubernetesApi", false)]
    [InlineData("Ssh", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CanHandle_OnlyMatchesLinuxTentacleStyles(string style, bool expected)
    {
        var strategy = new LinuxTentacleUpgradeStrategy(halibutClientFactory: null, observer: null);

        strategy.CanHandle(style).ShouldBe(expected);
    }
}
