using Squid.Core.Settings.GithubPackage;

namespace Squid.UnitTests.Settings;

public class CalamariGithubPackageSettingTests
{
    [Theory]
    [InlineData(null, "28.2.1")]
    [InlineData("", "28.2.1")]
    [InlineData("   ", "28.2.1")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("28.5.0", "28.5.0")]
    public void ResolvedVersion_ReturnsVersionOrDefault(string version, string expected)
    {
        var setting = new CalamariGithubPackageSetting { Version = version };

        setting.ResolvedVersion.ShouldBe(expected);
    }
}
