using Squid.Tentacle.ScriptExecution;

namespace Squid.Tentacle.Tests.ScriptExecution;

public class EosMarkerTests
{
    [Fact]
    public void GenerateMarkerToken_Returns32CharHex()
    {
        var token = EosMarker.GenerateMarkerToken();

        token.Length.ShouldBe(32);
        token.ShouldMatch("^[a-f0-9]{32}$");
    }

    [Fact]
    public void GenerateMarkerToken_UniquePerCall()
    {
        var t1 = EosMarker.GenerateMarkerToken();
        var t2 = EosMarker.GenerateMarkerToken();

        t1.ShouldNotBe(t2);
    }

    [Fact]
    public void WrapScript_AppendsEosMarkerLines()
    {
        var token = "abc123";
        var wrapped = EosMarker.WrapScript("echo hello", token);

        wrapped.ShouldContain("echo hello");
        wrapped.ShouldContain("__squid_exit_code__=$?");
        wrapped.ShouldContain($"echo \"EOS-{token}<<>>${{__squid_exit_code__}}\"");
        wrapped.ShouldContain("exit $__squid_exit_code__");
    }

    [Fact]
    public void WrapScript_PreservesOriginalScript()
    {
        var original = "#!/bin/bash\nset -e\necho hello\nkubectl apply -f .";
        var wrapped = EosMarker.WrapScript(original, "token123");

        wrapped.ShouldStartWith(original);
    }

    [Theory]
    [InlineData("EOS-abc123<<>>0", "abc123", 0)]
    [InlineData("EOS-abc123<<>>1", "abc123", 1)]
    [InlineData("EOS-abc123<<>>137", "abc123", 137)]
    [InlineData("EOS-abc123<<>>-1", "abc123", -1)]
    [InlineData("EOS-abc123<<>>-43", "abc123", -43)]
    public void TryParse_ValidMarker_ReturnsExitCode(string line, string token, int expectedExitCode)
    {
        var result = EosMarker.TryParse(line, token);

        result.ShouldNotBeNull();
        result.ExitCode.ShouldBe(expectedExitCode);
    }

    [Theory]
    [InlineData("", "abc123")]
    [InlineData(null, "abc123")]
    [InlineData("EOS-wrong<<>>0", "abc123")]
    [InlineData("echo hello", "abc123")]
    [InlineData("EOS-abc123<<>>notanumber", "abc123")]
    [InlineData("EOS-abc123<<>>", "abc123")]
    [InlineData("PREFIX-EOS-abc123<<>>0", "abc123")]
    public void TryParse_InvalidMarker_ReturnsNull(string line, string token)
    {
        EosMarker.TryParse(line, token).ShouldBeNull();
    }

    [Fact]
    public void TryParse_DifferentToken_ReturnsNull()
    {
        EosMarker.TryParse("EOS-token1<<>>0", "token2").ShouldBeNull();
    }
}
