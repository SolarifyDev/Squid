using Squid.Core.Services.DeploymentExecution.Infrastructure;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class YamlSafeScalarTests
{
    [Fact]
    public void Escape_PlainValue_DoubleQuoted()
    {
        YamlSafeScalar.Escape("my-app").ShouldBe("\"my-app\"");
    }

    [Fact]
    public void Escape_NullOrEmpty_ReturnsEmptyQuoted()
    {
        YamlSafeScalar.Escape(null).ShouldBe("\"\"");
        YamlSafeScalar.Escape(string.Empty).ShouldBe("\"\"");
    }

    [Fact]
    public void Escape_BackslashEscaped()
    {
        YamlSafeScalar.Escape(@"path\to\file").ShouldBe(@"""path\\to\\file""");
    }

    [Fact]
    public void Escape_DoubleQuoteEscaped()
    {
        YamlSafeScalar.Escape("say \"hello\"").ShouldBe(@"""say \""hello\""""");
    }

    [Theory]
    [InlineData("\n", @"""\n""")]
    [InlineData("\r", @"""\r""")]
    [InlineData("\t", @"""\t""")]
    public void Escape_ControlCharacters_Escaped(string input, string expected)
    {
        YamlSafeScalar.Escape(input).ShouldBe(expected);
    }

    [Fact]
    public void Escape_NewlineInjection_Escaped()
    {
        var malicious = "my-app\n  malicious: true";
        var result = YamlSafeScalar.Escape(malicious);

        result.ShouldBe("\"my-app\\n  malicious: true\"");
        result.ShouldNotContain("\n");
    }

    [Fact]
    public void Escape_ColonSpace_SafeInYaml()
    {
        YamlSafeScalar.Escape("key: value").ShouldBe("\"key: value\"");
    }

    [Fact]
    public void Escape_YamlSpecialChars_QuotedSafely()
    {
        YamlSafeScalar.Escape("# comment").ShouldBe("\"# comment\"");
        YamlSafeScalar.Escape("[array]").ShouldBe("\"[array]\"");
        YamlSafeScalar.Escape("{object}").ShouldBe("\"{object}\"");
        YamlSafeScalar.Escape("*anchor").ShouldBe("\"*anchor\"");
        YamlSafeScalar.Escape("&alias").ShouldBe("\"&alias\"");
    }

    [Fact]
    public void Escape_CombinedSpecialChars_AllEscaped()
    {
        var input = "line1\nline2\twith\\backslash and \"quotes\"";
        var result = YamlSafeScalar.Escape(input);

        result.ShouldBe("\"line1\\nline2\\twith\\\\backslash and \\\"quotes\\\"\"");
    }
}
