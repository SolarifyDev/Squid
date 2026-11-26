using Squid.Core.Services.Packages.Versioning;
using Squid.Core.Services.Packages.Versioning.Exceptions;
using Squid.Message.Constants;

namespace Squid.UnitTests.Services.Packages.Versioning;

public class VersionTemplateParserTests
{
    [Fact]
    public void Parse_LiteralThenToken()
    {
        var parts = VersionTemplateParser.Parse("1.0.{counter}");

        parts.Count.ShouldBe(2);
        parts[0].ShouldBe(VersionTemplatePart.OfLiteral("1.0."));
        parts[1].ShouldBe(VersionTemplatePart.OfToken("counter", null));
    }

    [Fact]
    public void Parse_TokensWithArgumentsAndLiteralBetween()
    {
        var parts = VersionTemplateParser.Parse("{date:yyyy.MM.dd}.{git.sha:7}");

        parts.ShouldBe([VersionTemplatePart.OfToken("date", "yyyy.MM.dd"), VersionTemplatePart.OfLiteral("."), VersionTemplatePart.OfToken("git.sha", "7")]);
    }

    [Fact]
    public void Parse_AdjacentTokensHaveNoLiteralBetween()
    {
        var parts = VersionTemplateParser.Parse("{input}{git.sha:9}");

        parts.ShouldBe([VersionTemplatePart.OfToken("input", null), VersionTemplatePart.OfToken("git.sha", "9")]);
    }

    [Fact]
    public void Parse_ArgumentMayContainColons_OnlyTheFirstSplits()
    {
        VersionTemplateParser.Parse("{date:HH:mm}").ShouldBe([VersionTemplatePart.OfToken("date", "HH:mm")]);
    }

    [Fact]
    public void Parse_TemplateWithoutTokensIsOneLiteral()
    {
        VersionTemplateParser.Parse("2.0.0").ShouldBe([VersionTemplatePart.OfLiteral("2.0.0")]);
    }

    [Theory]
    [InlineData("", "the template is empty")]
    [InlineData("   ", "the template is empty")]
    [InlineData("1.0.{counter", "'{' at position 5 has no closing '}'")]
    [InlineData("1.0.}", "'}' at position 5 has no opening '{'")]
    [InlineData("{{counter}}", "'{' at position 1 is followed by another '{' before it is closed")]
    [InlineData("1.{}", "'{}' does not start with a token name (letters, digits and '.', beginning with a letter)")]
    [InlineData("{:x}", "'{:x}' does not start with a token name (letters, digits and '.', beginning with a letter)")]
    [InlineData("{1abc}", "'{1abc}' does not start with a token name (letters, digits and '.', beginning with a letter)")]
    [InlineData("1.0.{counter:}", "'{counter:}' has a ':' but no argument after it")]
    public void Parse_Malformed_SaysWhatIsWrong(string template, string reason)
    {
        var ex = Should.Throw<MalformedVersionTemplateException>(() => VersionTemplateParser.Parse(template));

        ex.Message.ShouldBe($"Version template '{template}' is malformed: {reason}");
    }

    [Fact]
    public void Parse_TooLong_IsRejected()
    {
        var template = new string('1', VersionTemplateParser.MaxLength + 1);

        Should.Throw<MalformedVersionTemplateException>(() => VersionTemplateParser.Parse(template)).Message.ShouldContain("the limit is 256");
    }

    [Fact]
    public void DefaultTemplate_IsPinned()
    {
        // A package without a rule gets this version scheme; changing it changes the versions of every package nobody configured.
        PackageVersioning.DefaultTemplate.ShouldBe("1.0.{counter}");
    }
}
