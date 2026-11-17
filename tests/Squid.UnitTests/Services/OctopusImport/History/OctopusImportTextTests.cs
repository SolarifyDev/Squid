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

    [Theory]
    [InlineData(null, 3, null)]
    [InlineData("abc", 3, "abc")]
    [InlineData("abcdef", 3, "abc")]
    [InlineData("ab\U0001F600cd", 3, "ab")]
    [InlineData("ab\U0001F600cd", 4, "ab\U0001F600")]
    [InlineData("a\U0001F600", 1, "a")]
    public void Truncate_NeverSplitsASurrogatePair(string value, int maxLength, string expected)
        => OctopusImportText.Truncate(value, maxLength).ShouldBe(expected);

    [Fact]
    public void Truncate_EmojiAtTheCut_LeavesValidUtf16()
    {
        var value = new string('x', 199) + "\U0001F600" + "tail";

        var truncated = OctopusImportText.Truncate(value, 200);

        truncated.Length.ShouldBeLessThanOrEqualTo(200);
        char.IsHighSurrogate(truncated[^1]).ShouldBeFalse();
        Should.NotThrow(() => new System.Text.UTF8Encoding(false, true).GetBytes(truncated));
    }
}
