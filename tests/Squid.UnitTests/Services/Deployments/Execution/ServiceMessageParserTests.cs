using System;
using System.Collections.Generic;
using System.Text;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Script.ServiceMessages;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class ServiceMessageParserTests
{
    private readonly ServiceMessageParser _parser = new();

    [Fact]
    public void Parse_StandardMessage_Extracted()
    {
        var lines = new[] { "##squid[setVariable name='MyVar' value='Hello']" };

        var result = _parser.ParseOutputVariables(lines);

        result.Count.ShouldBe(1);
        result["MyVar"].Name.ShouldBe("MyVar");
        result["MyVar"].Value.ShouldBe("Hello");
        result["MyVar"].IsSensitive.ShouldBeFalse();
    }

    [Fact]
    public void Parse_SensitiveFlag_Parsed()
    {
        var lines = new[] { "##squid[setVariable name='Secret' value='pw123' sensitive='True']" };

        var result = _parser.ParseOutputVariables(lines);

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

        var result = _parser.ParseOutputVariables(lines);

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

        var result = _parser.ParseOutputVariables(lines);

        result.Count.ShouldBe(1);
        result["X"].Value.ShouldBe("second");
    }

    [Fact]
    public void Parse_EmptyValue_Parsed()
    {
        var lines = new[] { "##squid[setVariable name='Empty' value='']" };

        var result = _parser.ParseOutputVariables(lines);

        result["Empty"].Value.ShouldBe("");
    }

    [Fact]
    public void Parse_NoMessages_Empty()
    {
        var lines = new[] { "Some regular log output", "Another line" };

        var result = _parser.ParseOutputVariables(lines);

        result.Count.ShouldBe(0);
    }

    [Fact]
    public void Parse_NullInput_Empty()
    {
        var result = _parser.ParseOutputVariables(null);

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

        var result = _parser.ParseOutputVariables(lines);

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

        var result = _parser.ParseOutputVariables(lines);

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

        var result = _parser.TryParseOutputVariable(line);

        result.ShouldNotBeNull();
        result.Name.ShouldBe("MyVar");
        result.Value.ShouldBe("MyValue");
        result.IsSensitive.ShouldBeFalse();
    }

    [Fact]
    public void TryParse_LegacyFormat_StillWorks()
    {
        var line = "##squid[setVariable name='OldVar' value='OldValue' sensitive='True']";

        var result = _parser.TryParseOutputVariable(line);

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

        var result = _parser.TryParseOutputVariable(line);

        result.ShouldNotBeNull();
        result.Value.ShouldBe("value'with'quotes");
    }

    [Fact]
    public void TryParse_ValueWithNewlines_Base64Succeeds()
    {
        var nameB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("MultiLineVar"));
        var valueB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("line1\nline2\nline3"));
        var line = $"##squid[setVariable name=\"{nameB64}\" value=\"{valueB64}\"]";

        var result = _parser.TryParseOutputVariable(line);

        result.ShouldNotBeNull();
        result.Value.ShouldBe("line1\nline2\nline3");
    }

    [Fact]
    public void TryParse_InvalidBase64_ReturnsNull()
    {
        var line = "##squid[setVariable name=\"not-valid-base64!!!\" value=\"also-invalid\"]";

        var result = _parser.TryParseOutputVariable(line);

        result.ShouldBeNull();
    }

    [Fact]
    public void TryParse_Base64WithSensitive_DecodesFlag()
    {
        var nameB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("SecretVar"));
        var valueB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("secret-value"));
        var sensitiveB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("True"));
        var line = $"##squid[setVariable name=\"{nameB64}\" value=\"{valueB64}\" sensitive=\"{sensitiveB64}\"]";

        var result = _parser.TryParseOutputVariable(line);

        result.ShouldNotBeNull();
        result.IsSensitive.ShouldBeTrue();
    }

    // ========== Structured ParsedServiceMessage ==========

    [Fact]
    public void ParseMessages_SetVariable_LegacyFormat_YieldsStructured()
    {
        var lines = new[] { "##squid[setVariable name='MyVar' value='Hello' sensitive='True']" };

        var messages = _parser.ParseMessages(lines);

        messages.Count.ShouldBe(1);
        messages[0].Kind.ShouldBe(ServiceMessageKind.SetVariable);
        messages[0].Verb.ShouldBe("setVariable");
        messages[0].GetAttribute("name").ShouldBe("MyVar");
        messages[0].GetAttribute("value").ShouldBe("Hello");
        messages[0].GetAttribute("sensitive").ShouldBe("True");
    }

    [Fact]
    public void ParseMessages_SetVariable_Base64Format_DecodesAttributes()
    {
        var nameB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("MyVar"));
        var valueB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("Hello"));
        var line = $"##squid[setVariable name=\"{nameB64}\" value=\"{valueB64}\"]";

        var message = _parser.TryParseMessage(line);

        message.ShouldNotBeNull();
        message.Kind.ShouldBe(ServiceMessageKind.SetVariable);
        message.GetAttribute("name").ShouldBe("MyVar");
        message.GetAttribute("value").ShouldBe("Hello");
    }

    [Fact]
    public void ParseMessages_CreateArtifact_Base64Format_Decoded()
    {
        var pathB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("/tmp/output.log"));
        var nameB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("output.log"));
        var lengthB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("2048"));
        var line = $"##squid[createArtifact path=\"{pathB64}\" name=\"{nameB64}\" length=\"{lengthB64}\"]";

        var message = _parser.TryParseMessage(line);

        message.ShouldNotBeNull();
        message.Kind.ShouldBe(ServiceMessageKind.CreateArtifact);
        message.Verb.ShouldBe("createArtifact");
        message.GetAttribute("path").ShouldBe("/tmp/output.log");
        message.GetAttribute("name").ShouldBe("output.log");
        message.GetAttribute("length").ShouldBe("2048");
    }

    [Fact]
    public void ParseMessages_StepFailed_LegacyFormat_Parsed()
    {
        var line = "##squid[stepFailed reason='deployment aborted by user']";

        var message = _parser.TryParseMessage(line);

        message.ShouldNotBeNull();
        message.Kind.ShouldBe(ServiceMessageKind.StepFailed);
        message.Verb.ShouldBe("stepFailed");
        message.GetAttribute("reason").ShouldBe("deployment aborted by user");
    }

    [Fact]
    public void ParseMessages_StdWarning_NoAttributes_Parsed()
    {
        var line = "##squid[stdWarning]";

        var message = _parser.TryParseMessage(line);

        message.ShouldNotBeNull();
        message.Kind.ShouldBe(ServiceMessageKind.StdWarning);
        message.Verb.ShouldBe("stdWarning");
        message.Attributes.Count.ShouldBe(0);
    }

    [Fact]
    public void ParseMessages_MixedKinds_ReturnsAllInOrder()
    {
        var lines = new[]
        {
            "##squid[setVariable name='Result' value='OK']",
            "plain log line",
            "##squid[stdWarning]",
            "##squid[stepFailed reason='timeout']"
        };

        var messages = _parser.ParseMessages(lines);

        messages.Count.ShouldBe(3);
        messages[0].Kind.ShouldBe(ServiceMessageKind.SetVariable);
        messages[1].Kind.ShouldBe(ServiceMessageKind.StdWarning);
        messages[2].Kind.ShouldBe(ServiceMessageKind.StepFailed);
    }

    [Fact]
    public void ParseMessages_UnknownVerb_ReturnsUnknownKind()
    {
        var line = "##squid[somethingElse foo='bar']";

        var message = _parser.TryParseMessage(line);

        message.ShouldNotBeNull();
        message.Kind.ShouldBe(ServiceMessageKind.Unknown);
        message.Verb.ShouldBe("somethingElse");
        message.GetAttribute("foo").ShouldBe("bar");
    }
}
