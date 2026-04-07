using Squid.Core.Services.DeploymentExecution.Ssh;

namespace Squid.UnitTests.Services.Deployments.Ssh;

public class SshFingerprintNormalizationTests
{
    [Theory]
    [InlineData("abc123", "abc123")]
    [InlineData("SHA256:abc123", "abc123")]
    [InlineData("sha256:abc123", "abc123")]
    [InlineData("MD5:aa:bb:cc:dd", "aabbccdd")]
    [InlineData("md5:aa:bb:cc:dd", "aabbccdd")]
    [InlineData("AA:BB:CC:DD", "AABBCCDD")]
    [InlineData("AA-BB-CC-DD", "AABBCCDD")]
    [InlineData("  SHA256:abc123  ", "abc123")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeFingerprint_ReturnsExpected(string input, string expected)
    {
        SshConnectionScope.NormalizeFingerprint(input).ShouldBe(expected);
    }

    [Fact]
    public void NormalizeFingerprint_Sha256PrefixRemoved_MatchesRaw()
    {
        var raw = "W3TpoQ1koYZ8scRhKMuSJw7IHX48c4j0i6LryQ6nShU";
        var prefixed = $"SHA256:{raw}";

        var normalizedRaw = SshConnectionScope.NormalizeFingerprint(raw);
        var normalizedPrefixed = SshConnectionScope.NormalizeFingerprint(prefixed);

        normalizedRaw.ShouldBe(normalizedPrefixed);
    }

    [Fact]
    public void NormalizeFingerprint_ColonSeparatedAndPlain_Match()
    {
        var colonSeparated = "aa:bb:cc:dd:ee:ff";
        var plain = "aabbccddeeff";

        var normalized1 = SshConnectionScope.NormalizeFingerprint(colonSeparated);
        var normalized2 = SshConnectionScope.NormalizeFingerprint(plain);

        normalized1.ShouldBe(normalized2);
    }

    [Fact]
    public void NormalizeFingerprint_DashSeparatedAndPlain_Match()
    {
        var dashSeparated = "aa-bb-cc-dd-ee-ff";
        var plain = "aabbccddeeff";

        var normalized1 = SshConnectionScope.NormalizeFingerprint(dashSeparated);
        var normalized2 = SshConnectionScope.NormalizeFingerprint(plain);

        normalized1.ShouldBe(normalized2);
    }
}
