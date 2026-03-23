using System;
using System.Collections.Generic;
using System.Text;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Script;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class ServiceMessageParserTests
{
    [Fact]
    public void Parse_StandardMessage_Extracted()
    {
        var lines = new[] { "##squid[setVariable name='MyVar' value='Hello']" };

        var result = ServiceMessageParser.ParseOutputVariables(lines);

        result.Count.ShouldBe(1);
        result["MyVar"].Name.ShouldBe("MyVar");
        result["MyVar"].Value.ShouldBe("Hello");
        result["MyVar"].IsSensitive.ShouldBeFalse();
    }

    [Fact]
    public void Parse_SensitiveFlag_Parsed()
    {
        var lines = new[] { "##squid[setVariable name='Secret' value='pw123' sensitive='True']" };

        var result = ServiceMessageParser.ParseOutputVariables(lines);

        result["Secret"].IsSensitive.ShouldBeTrue();
    }

    [Fact]
    public void Parse_MultipleMessages_AllParsed()
    {
        var lines = new[]
        {
            "##squid[setVariable name='A' value='1']",
            "##squid[setVariable name='B' value='2']"
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
            "##squid[setVariable name='X' value='first']",
            "##squid[setVariable name='X' value='second']"
        };

        var result = ServiceMessageParser.ParseOutputVariables(lines);

        result.Count.ShouldBe(1);
        result["X"].Value.ShouldBe("second");
    }

    [Fact]
    public void Parse_EmptyValue_Parsed()
    {
        var lines = new[] { "##squid[setVariable name='Empty' value='']" };

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
            "##squid[setVariable name='Result' value='OK']",
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
            "##squid[setVariable name='Good' value='yes']",
            "##squid[setVariable name=]",
            "##squid[somethingElse name='X']"
        };

        var result = ServiceMessageParser.ParseOutputVariables(lines);

        result.Count.ShouldBe(1);
        result["Good"].Value.ShouldBe("yes");
    }

    // ========== TryParse: Base64 Format ==========

    [Fact]
    public void TryParse_Base64Format_DecodesCorrectly()
    {
        var nameB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("MyVar"));
        var valueB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("MyValue"));
        var line = $"##squid[setVariable name=\"{nameB64}\" value=\"{valueB64}\"]";

        var result = ServiceMessageParser.TryParse(line);

        result.ShouldNotBeNull();
        result.Name.ShouldBe("MyVar");
        result.Value.ShouldBe("MyValue");
        result.IsSensitive.ShouldBeFalse();
    }

    [Fact]
    public void TryParse_LegacyFormat_StillWorks()
    {
        var line = "##squid[setVariable name='OldVar' value='OldValue' sensitive='True']";

        var result = ServiceMessageParser.TryParse(line);

        result.ShouldNotBeNull();
        result.Name.ShouldBe("OldVar");
        result.Value.ShouldBe("OldValue");
        result.IsSensitive.ShouldBeTrue();
    }

    [Fact]
    public void TryParse_ValueWithSingleQuotes_Base64Succeeds()
    {
        var nameB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("TestVar"));
        var valueB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("value'with'quotes"));
        var line = $"##squid[setVariable name=\"{nameB64}\" value=\"{valueB64}\"]";

        var result = ServiceMessageParser.TryParse(line);

        result.ShouldNotBeNull();
        result.Value.ShouldBe("value'with'quotes");
    }

    [Fact]
    public void TryParse_ValueWithNewlines_Base64Succeeds()
    {
        var nameB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("MultiLineVar"));
        var valueB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("line1\nline2\nline3"));
        var line = $"##squid[setVariable name=\"{nameB64}\" value=\"{valueB64}\"]";

        var result = ServiceMessageParser.TryParse(line);

        result.ShouldNotBeNull();
        result.Value.ShouldBe("line1\nline2\nline3");
    }

    [Fact]
    public void TryParse_InvalidBase64_ReturnsNull()
    {
        var line = "##squid[setVariable name=\"not-valid-base64!!!\" value=\"also-invalid\"]";

        var result = ServiceMessageParser.TryParse(line);

        result.ShouldBeNull();
    }

    [Fact]
    public void TryParse_Base64WithSensitive_DecodesFlag()
    {
        var nameB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("SecretVar"));
        var valueB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("secret-value"));
        var sensitiveB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("True"));
        var line = $"##squid[setVariable name=\"{nameB64}\" value=\"{valueB64}\" sensitive=\"{sensitiveB64}\"]";

        var result = ServiceMessageParser.TryParse(line);

        result.ShouldNotBeNull();
        result.IsSensitive.ShouldBeTrue();
    }

    // ========== Backward Compatibility — ##octopus[ prefix ==========

    [Fact]
    public void TryParse_OctopusPrefix_LegacyFormat_ParsesCorrectly()
    {
        var line = "##octopus[setVariable name='MyVar' value='Hello' sensitive='True']";

        var result = ServiceMessageParser.TryParse(line);

        result.ShouldNotBeNull();
        result.Name.ShouldBe("MyVar");
        result.Value.ShouldBe("Hello");
        result.IsSensitive.ShouldBeTrue();
    }

    [Fact]
    public void TryParse_OctopusPrefix_Base64Format_ParsesCorrectly()
    {
        var nameB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("OctoVar"));
        var valueB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("OctoValue"));
        var line = $"##octopus[setVariable name=\"{nameB64}\" value=\"{valueB64}\"]";

        var result = ServiceMessageParser.TryParse(line);

        result.ShouldNotBeNull();
        result.Name.ShouldBe("OctoVar");
        result.Value.ShouldBe("OctoValue");
    }

    [Fact]
    public void ParseOutputVariables_OctopusPrefix_IncludedInResults()
    {
        var lines = new[]
        {
            "##squid[setVariable name='SquidVar' value='1']",
            "##octopus[setVariable name='OctoVar' value='2']"
        };

        var result = ServiceMessageParser.ParseOutputVariables(lines);

        result.Count.ShouldBe(2);
        result["SquidVar"].Value.ShouldBe("1");
        result["OctoVar"].Value.ShouldBe("2");
    }
}
