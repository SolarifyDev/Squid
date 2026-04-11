using System;
using System.Linq;
using System.Text;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.ScriptExecution;

namespace Squid.Tentacle.Tests.ScriptExecution;

public class PodLogLineParserTests
{
    // ========================================================================
    // Non-directive lines — pass through as StdOut
    // ========================================================================

    [Theory]
    [InlineData("hello world")]
    [InlineData("  leading whitespace")]
    [InlineData("kubectl apply -f manifest.yaml")]
    [InlineData("## not a directive")]
    [InlineData("##squid malformed no brackets")]
    public void Parse_RegularLine_ReturnsStdOutNonDirective(string line)
    {
        var result = PodLogLineParser.Parse(line);

        result.IsDirective.ShouldBeFalse();
        result.Source.ShouldBe(ProcessOutputSource.StdOut);
        result.Text.ShouldBe(line);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Parse_NullOrEmpty_ReturnsStdOutNonDirective(string line)
    {
        var result = PodLogLineParser.Parse(line);

        result.IsDirective.ShouldBeFalse();
        result.Source.ShouldBe(ProcessOutputSource.StdOut);
    }

    // ========================================================================
    // setVariable directive — the most critical directive
    // ========================================================================

    [Fact]
    public void Parse_SetVariable_PlainText_ParsesNameAndValue()
    {
        var line = "##squid[setVariable name='MyVar' value='hello']";

        var result = PodLogLineParser.Parse(line);

        result.IsDirective.ShouldBeTrue();
        result.DirectiveType.ShouldBe("setVariable");
        result.DirectiveArgs.ShouldContainKeyAndValue("name", "MyVar");
        result.DirectiveArgs.ShouldContainKeyAndValue("value", "hello");
    }

    [Fact]
    public void Parse_SetVariable_Base64Encoded_DecodesValues()
    {
        var nameB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("MyVar"));
        var valueB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("secret-value"));

        var line = $"##squid[setVariable name='{nameB64}' value='{valueB64}']";

        var result = PodLogLineParser.Parse(line);

        result.IsDirective.ShouldBeTrue();
        result.DirectiveArgs.ShouldContainKeyAndValue("name", "MyVar");
        result.DirectiveArgs.ShouldContainKeyAndValue("value", "secret-value");
    }

    [Fact]
    public void Parse_SetVariable_WithSensitiveFlag()
    {
        var line = "##squid[setVariable name='Token' value='abc123' sensitive='True']";

        var result = PodLogLineParser.Parse(line);

        result.DirectiveArgs.ShouldContainKeyAndValue("sensitive", "True");
    }

    // ========================================================================
    // stdout-* directives — source reclassification
    // ========================================================================

    [Theory]
    [InlineData("##squid[stdout-error]", ProcessOutputSource.StdErr)]
    [InlineData("##squid[stdout-warning]", ProcessOutputSource.StdErr)]
    [InlineData("##squid[stdout-highlight]", ProcessOutputSource.StdOut)]
    [InlineData("##squid[stdout-default]", ProcessOutputSource.StdOut)]
    public void Parse_StdoutDirective_ResolvesCorrectSource(string line, ProcessOutputSource expectedSource)
    {
        var result = PodLogLineParser.Parse(line);

        result.IsDirective.ShouldBeTrue();
        result.Source.ShouldBe(expectedSource);
    }

    // ========================================================================
    // progress directive
    // ========================================================================

    [Fact]
    public void Parse_Progress_ParsesPercentage()
    {
        var line = "##squid[progress percentage='50']";

        var result = PodLogLineParser.Parse(line);

        result.IsDirective.ShouldBeTrue();
        result.DirectiveType.ShouldBe("progress");
        result.DirectiveArgs.ShouldContainKeyAndValue("percentage", "50");
    }

    // ========================================================================
    // Directive with no arguments
    // ========================================================================

    [Fact]
    public void Parse_DirectiveWithNoArgs_HasEmptyArgsDictionary()
    {
        var line = "##squid[stdout-highlight]";

        var result = PodLogLineParser.Parse(line);

        result.IsDirective.ShouldBeTrue();
        result.DirectiveType.ShouldBe("stdout-highlight");
        result.DirectiveArgs.ShouldBeEmpty();
    }

    // ========================================================================
    // Arg key case insensitivity
    // ========================================================================

    [Fact]
    public void Parse_ArgKeysAreCaseInsensitive()
    {
        var line = "##squid[setVariable Name='x' Value='y']";

        var result = PodLogLineParser.Parse(line);

        result.DirectiveArgs.ShouldContainKey("name");
        result.DirectiveArgs.ShouldContainKey("value");
    }

    // ========================================================================
    // Edge cases
    // ========================================================================

    [Fact]
    public void Parse_NonBase64Value_KeepsRawValue()
    {
        // "not-base64!!!" is not valid base64 — should keep raw
        var line = "##squid[setVariable name='Var' value='not-base64!!!']";

        var result = PodLogLineParser.Parse(line);

        result.DirectiveArgs.ShouldContainKeyAndValue("value", "not-base64!!!");
    }

    [Fact]
    public void Parse_EmptyValue_KeepsEmpty()
    {
        var line = "##squid[setVariable name='Var' value='']";

        var result = PodLogLineParser.Parse(line);

        result.DirectiveArgs.ShouldContainKeyAndValue("value", "");
    }

    [Fact]
    public void Parse_CreateArtifact_ParsesCorrectly()
    {
        var pathB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("/tmp/artifact.zip"));
        var line = $"##squid[createArtifact path='{pathB64}' length='1024']";

        var result = PodLogLineParser.Parse(line);

        result.IsDirective.ShouldBeTrue();
        result.DirectiveType.ShouldBe("createArtifact");
        result.DirectiveArgs.ShouldContainKeyAndValue("path", "/tmp/artifact.zip");
    }

    [Fact]
    public void Parse_ValueWithSingleQuotes_RequiresBase64Encoding()
    {
        // Values containing single quotes cannot be represented directly (arg regex uses [^']*)
        // They must be base64 encoded — this matches the Squid convention
        var valueWithQuote = "it's a test";
        var valueB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(valueWithQuote));
        var line = $"##squid[setVariable name='Var' value='{valueB64}']";

        var result = PodLogLineParser.Parse(line);

        result.DirectiveArgs.ShouldContainKeyAndValue("value", valueWithQuote);
    }

    [Fact]
    public void Parse_MultipleDirectivesInSequence_EachParsedIndependently()
    {
        var lines = new[]
        {
            "##squid[setVariable name='A' value='1']",
            "regular output",
            "##squid[setVariable name='B' value='2']"
        };

        var results = lines.Select(PodLogLineParser.Parse).ToArray();

        results[0].IsDirective.ShouldBeTrue();
        results[0].DirectiveArgs.ShouldContainKeyAndValue("name", "A");
        results[1].IsDirective.ShouldBeFalse();
        results[2].IsDirective.ShouldBeTrue();
        results[2].DirectiveArgs.ShouldContainKeyAndValue("name", "B");
    }

}
