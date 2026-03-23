using Squid.Tentacle.ScriptExecution;

namespace Squid.Tentacle.Tests.ScriptExecution;

public class SensitiveOutputMaskerTests
{
    [Fact]
    public void MaskLine_ContainsSecret_Replaced()
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal) { "my-secret-password" };

        var result = SensitiveOutputMasker.MaskLine("Connection string: server=host;password=my-secret-password;", secrets);

        result.ShouldBe("Connection string: server=host;password=********;");
    }

    [Fact]
    public void MaskLine_MultipleSecrets_AllReplaced()
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal) { "secret1", "secret2" };

        var result = SensitiveOutputMasker.MaskLine("Values: secret1 and secret2 in output", secrets);

        result.ShouldBe("Values: ******** and ******** in output");
    }

    [Fact]
    public void MaskLine_NoSecrets_Unchanged()
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal) { "not-present" };

        var result = SensitiveOutputMasker.MaskLine("just normal output", secrets);

        result.ShouldBe("just normal output");
    }

    [Theory]
    [InlineData("ab", "Values: ab in output")]
    [InlineData("env", "Values: env in output")]
    [InlineData("dev", "Values: dev in output")]
    [InlineData("api", "Values: api in output")]
    public void MaskLine_ShortValue_NotMasked(string shortSecret, string expectedOutput)
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal) { shortSecret };

        var result = SensitiveOutputMasker.MaskLine(expectedOutput, secrets);

        result.ShouldBe(expectedOutput);
    }

    [Fact]
    public void MaskLine_EmptyLine_ReturnsEmpty()
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal) { "secret" };

        SensitiveOutputMasker.MaskLine("", secrets).ShouldBe("");
    }

    [Fact]
    public void MaskLine_NullSet_ReturnsUnchanged()
    {
        SensitiveOutputMasker.MaskLine("some output", null).ShouldBe("some output");
    }

    [Fact]
    public void MaskLine_CaseSensitive_DoesNotMaskDifferentCase()
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal) { "Secret" };

        var result = SensitiveOutputMasker.MaskLine("secret is not Secret", secrets);

        result.ShouldBe("secret is not ********");
    }

    [Fact]
    public void MaskLine_LongerValueFirst_NoPartialCorruption()
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal) { "abcd", "abcdef" };

        var result = SensitiveOutputMasker.MaskLine("value=abcdef end", secrets);

        result.ShouldBe("value=******** end");
    }
}
