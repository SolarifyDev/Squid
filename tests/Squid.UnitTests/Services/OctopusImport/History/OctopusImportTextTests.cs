using Squid.Core.Services.OctopusImport.History;

namespace Squid.UnitTests.Services.OctopusImport.History;

public class OctopusImportTextTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("plain text", "plain text")]
    [InlineData("before\0after", "beforeafter")]
    [InlineData("\0W\0S\0L\0", "WSL")]
    [InlineData("\0\0", "")]
    public void StripNul_RemovesEveryNulCharacter(string value, string expected)
        => OctopusImportText.StripNul(value).ShouldBe(expected);

    [Fact]
    public void StripNul_Values_CleansStringsAndKeepsEverythingElse()
    {
        var bytes = new byte[] { 0, 1 };
        var time = DateTimeOffset.UnixEpoch;

        var cleaned = OctopusImportText.StripNul(new object[] { "a\0b", 42, null, bytes, time });

        cleaned.ShouldBe(new object[] { "ab", 42, null, bytes, time });
    }
}
