using System.Text.Json;
using Squid.Core.Services.DeploymentExecution.OpenClaw;
using Squid.Core.Services.Http.Clients;

namespace Squid.UnitTests.Services.Deployments.OpenClaw;

public class OpenClawExecutionStrategyTests
{
    // ========================================================================
    // ExtractJsonPath
    // ========================================================================

    [Fact]
    public void ExtractJsonPath_SimpleProperty_ReturnsValue()
    {
        var json = """{"ok":true,"result":{"summary":"done"}}""";

        OpenClawExecutionStrategy.ExtractJsonPath(json, "$.result.summary").ShouldBe("done");
    }

    [Fact]
    public void ExtractJsonPath_RootProperty_ReturnsValue()
    {
        var json = """{"ok":true}""";

        OpenClawExecutionStrategy.ExtractJsonPath(json, "$.ok").ShouldBe("true");
    }

    [Fact]
    public void ExtractJsonPath_MissingProperty_ReturnsNull()
    {
        var json = """{"ok":true}""";

        OpenClawExecutionStrategy.ExtractJsonPath(json, "$.missing").ShouldBeNull();
    }

    [Fact]
    public void ExtractJsonPath_NullJson_ReturnsNull()
    {
        OpenClawExecutionStrategy.ExtractJsonPath(null, "$.ok").ShouldBeNull();
    }

    [Fact]
    public void ExtractJsonPath_NullPath_ReturnsJson()
    {
        var json = """{"ok":true}""";

        OpenClawExecutionStrategy.ExtractJsonPath(json, null).ShouldBe(json);
    }

    [Fact]
    public void ExtractJsonPath_NestedNumericValue_ReturnsRawText()
    {
        var json = """{"result":{"count":42}}""";

        OpenClawExecutionStrategy.ExtractJsonPath(json, "$.result.count").ShouldBe("42");
    }

    // ========================================================================
    // EvaluateAssertion
    // ========================================================================

    [Theory]
    [InlineData("hello", "equals", "hello", true)]
    [InlineData("hello", "equals", "world", false)]
    [InlineData("Hello", "equalsignorecase", "hello", true)]
    [InlineData("hello world", "contains", "world", true)]
    [InlineData("hello", "contains", "xyz", false)]
    [InlineData("abc123", "matches", @"\d+", true)]
    [InlineData("abc", "matches", @"^\d+$", false)]
    [InlineData("hello", "notequals", "world", true)]
    [InlineData("hello", "notequals", "hello", false)]
    [InlineData("42", "greaterthan", "10", true)]
    [InlineData("5", "greaterthan", "10", false)]
    [InlineData("3", "lessthan", "10", true)]
    [InlineData("20", "lessthan", "10", false)]
    public void EvaluateAssertion_VariousOperators(string actual, string op, string expected, bool shouldPass)
    {
        OpenClawExecutionStrategy.EvaluateAssertion(actual, op, expected).ShouldBe(shouldPass);
    }

    [Fact]
    public void EvaluateAssertion_NullActual_ReturnsFalse()
    {
        OpenClawExecutionStrategy.EvaluateAssertion(null, "equals", "anything").ShouldBeFalse();
    }

    [Fact]
    public void EvaluateAssertion_UnknownOperator_DefaultsToEquals()
    {
        OpenClawExecutionStrategy.EvaluateAssertion("a", "unknown_op", "a").ShouldBeTrue();
        OpenClawExecutionStrategy.EvaluateAssertion("a", "unknown_op", "b").ShouldBeFalse();
    }

    // ========================================================================
    // RepairJsonStrings / ParseArgsJson
    // ========================================================================

    [Fact]
    public void ParseArgsJson_ValidJson_ParsesNormally()
    {
        var result = OpenClawClient.ParseArgsJson("""{"message":"hello","count":1}""");

        result.ShouldBeOfType<JsonElement>();
    }

    [Fact]
    public void ParseArgsJson_BrokenByQuotesInValue_RepairsAndParses()
    {
        // Simulates: {"message":"#{Var}"} where Var = Hello "world" today
        var broken = """{"message":"Hello "world" today"}""";
        var result = OpenClawClient.ParseArgsJson(broken);

        var elem = result.ShouldBeOfType<JsonElement>();
        elem.GetProperty("message").GetString().ShouldBe("""Hello "world" today""");
    }

    [Fact]
    public void ParseArgsJson_BrokenByNewlinesInValue_RepairsAndParses()
    {
        var broken = "{\"message\":\"Line1\nLine2\"}";
        var result = OpenClawClient.ParseArgsJson(broken);

        var elem = result.ShouldBeOfType<JsonElement>();
        elem.GetProperty("message").GetString().ShouldBe("Line1\nLine2");
    }

    [Fact]
    public void ParseArgsJson_MultipleFieldsWithBrokenValue_RepairsCorrectly()
    {
        var broken = """{"action":"send","channel":"wecom","message":"Hello "world" 🦞","dryRun":false}""";
        var result = OpenClawClient.ParseArgsJson(broken);

        var elem = result.ShouldBeOfType<JsonElement>();
        elem.GetProperty("action").GetString().ShouldBe("send");
        elem.GetProperty("channel").GetString().ShouldBe("wecom");
        elem.GetProperty("message").GetString().ShouldContain("Hello");
        elem.GetProperty("dryRun").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void ParseArgsJson_EmptyString_ReturnsEmptyObject()
    {
        OpenClawClient.ParseArgsJson("").ShouldNotBeNull();
        OpenClawClient.ParseArgsJson(null).ShouldNotBeNull();
    }

    // ========================================================================
    // BuildMessages
    // ========================================================================

    [Fact]
    public void BuildMessages_WithPromptOnly_ReturnsUserMessage()
    {
        var messages = OpenClawExecutionStrategy.BuildMessages("hello", null, null);

        messages.Count.ShouldBe(1);
        messages[0].Role.ShouldBe("user");
        messages[0].Content.ShouldBe("hello");
    }

    [Fact]
    public void BuildMessages_WithSystemAndPrompt_ReturnsSystemThenUser()
    {
        var messages = OpenClawExecutionStrategy.BuildMessages("hello", "You are helpful", null);

        messages.Count.ShouldBe(2);
        messages[0].Role.ShouldBe("system");
        messages[0].Content.ShouldBe("You are helpful");
        messages[1].Role.ShouldBe("user");
        messages[1].Content.ShouldBe("hello");
    }

    [Fact]
    public void BuildMessages_WithMessagesJson_ParsesAndReturnsDirectly()
    {
        var json = """[{"role":"user","content":"from json"},{"role":"assistant","content":"reply"}]""";

        var messages = OpenClawExecutionStrategy.BuildMessages("ignored", "ignored", json);

        messages.Count.ShouldBe(2);
        messages[0].Role.ShouldBe("user");
        messages[0].Content.ShouldBe("from json");
        messages[1].Role.ShouldBe("assistant");
        messages[1].Content.ShouldBe("reply");
    }

    [Fact]
    public void BuildMessages_WithInvalidMessagesJson_FallsBackToPrompt()
    {
        var messages = OpenClawExecutionStrategy.BuildMessages("fallback", null, "{bad-json}");

        messages.Count.ShouldBe(1);
        messages[0].Role.ShouldBe("user");
        messages[0].Content.ShouldBe("fallback");
    }

    [Fact]
    public void BuildMessages_WithEmptyPromptAndNoJson_ReturnsEmptyList()
    {
        var messages = OpenClawExecutionStrategy.BuildMessages(null, null, null);

        messages.ShouldBeEmpty();
    }
}
