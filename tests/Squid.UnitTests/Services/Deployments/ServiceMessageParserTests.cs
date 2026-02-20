using System.Collections.Generic;
using Squid.Core.Services.DeploymentExecution;

namespace Squid.UnitTests.Services.Deployments;

public class ServiceMessageParserTests
{
    [Fact]
    public void Parse_StandardMessage_Extracted()
    {
        var lines = new[] { "##octopus[setVariable name='MyVar' value='Hello']" };

        var result = ServiceMessageParser.ParseOutputVariables(lines);

        result.Count.ShouldBe(1);
        result["MyVar"].Name.ShouldBe("MyVar");
        result["MyVar"].Value.ShouldBe("Hello");
        result["MyVar"].IsSensitive.ShouldBeFalse();
    }

    [Fact]
    public void Parse_SensitiveFlag_Parsed()
    {
        var lines = new[] { "##octopus[setVariable name='Secret' value='pw123' sensitive='True']" };

        var result = ServiceMessageParser.ParseOutputVariables(lines);

        result["Secret"].IsSensitive.ShouldBeTrue();
    }

    [Fact]
    public void Parse_MultipleMessages_AllParsed()
    {
        var lines = new[]
        {
            "##octopus[setVariable name='A' value='1']",
            "##octopus[setVariable name='B' value='2']"
        };

        var result = ServiceMessageParser.ParseOutputVariables(lines);

        result.Count.ShouldBe(2);
        result["A"].Value.ShouldBe("1");
        result["B"].Value.ShouldBe("2");
    }

    [Fact]
    public void Parse_DuplicateName_LastWins()
    {
        var lines = new[]
        {
            "##octopus[setVariable name='X' value='first']",
            "##octopus[setVariable name='X' value='second']"
        };

        var result = ServiceMessageParser.ParseOutputVariables(lines);

        result.Count.ShouldBe(1);
        result["X"].Value.ShouldBe("second");
    }

    [Fact]
    public void Parse_EmptyValue_Parsed()
    {
        var lines = new[] { "##octopus[setVariable name='Empty' value='']" };

        var result = ServiceMessageParser.ParseOutputVariables(lines);

        result["Empty"].Value.ShouldBe("");
    }

    [Fact]
    public void Parse_NoMessages_Empty()
    {
        var lines = new[] { "Some regular log output", "Another line" };

        var result = ServiceMessageParser.ParseOutputVariables(lines);

        result.Count.ShouldBe(0);
    }

    [Fact]
    public void Parse_NullInput_Empty()
    {
        var result = ServiceMessageParser.ParseOutputVariables(null);

        result.Count.ShouldBe(0);
    }

    [Fact]
    public void Parse_MixedLines_OnlyServiceMessages()
    {
        var lines = new[]
        {
            "Starting deployment...",
            "##octopus[setVariable name='Result' value='OK']",
            "Deployment complete."
        };

        var result = ServiceMessageParser.ParseOutputVariables(lines);

        result.Count.ShouldBe(1);
        result["Result"].Value.ShouldBe("OK");
    }

    [Fact]
    public void Parse_MalformedMessage_Skipped()
    {
        var lines = new[]
        {
            "##octopus[setVariable name='Good' value='yes']",
            "##octopus[setVariable name=]",
            "##octopus[somethingElse name='X']"
        };

        var result = ServiceMessageParser.ParseOutputVariables(lines);

        result.Count.ShouldBe(1);
        result["Good"].Value.ShouldBe("yes");
    }
}
