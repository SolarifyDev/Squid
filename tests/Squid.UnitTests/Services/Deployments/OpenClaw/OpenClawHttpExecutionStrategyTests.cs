using Squid.Core.Services.DeploymentExecution.OpenClaw;

namespace Squid.UnitTests.Services.Deployments.OpenClaw;

public class OpenClawHttpExecutionStrategyTests
{
    // ========================================================================
    // ExtractJsonPath
    // ========================================================================

    [Fact]
    public void ExtractJsonPath_SimpleProperty_ReturnsValue()
    {
        var json = """{"ok":true,"result":{"summary":"done"}}""";

        OpenClawHttpExecutionStrategy.ExtractJsonPath(json, "$.result.summary").ShouldBe("done");
    }

    [Fact]
    public void ExtractJsonPath_RootProperty_ReturnsValue()
    {
        var json = """{"ok":true}""";

        OpenClawHttpExecutionStrategy.ExtractJsonPath(json, "$.ok").ShouldBe("true");
    }

    [Fact]
    public void ExtractJsonPath_MissingProperty_ReturnsNull()
    {
        var json = """{"ok":true}""";

        OpenClawHttpExecutionStrategy.ExtractJsonPath(json, "$.missing").ShouldBeNull();
    }

    [Fact]
    public void ExtractJsonPath_NullJson_ReturnsNull()
    {
        OpenClawHttpExecutionStrategy.ExtractJsonPath(null, "$.ok").ShouldBeNull();
    }

    [Fact]
    public void ExtractJsonPath_NullPath_ReturnsJson()
    {
        var json = """{"ok":true}""";

        OpenClawHttpExecutionStrategy.ExtractJsonPath(json, null).ShouldBe(json);
    }

    [Fact]
    public void ExtractJsonPath_NestedNumericValue_ReturnsRawText()
    {
        var json = """{"result":{"count":42}}""";

        OpenClawHttpExecutionStrategy.ExtractJsonPath(json, "$.result.count").ShouldBe("42");
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
        OpenClawHttpExecutionStrategy.EvaluateAssertion(actual, op, expected).ShouldBe(shouldPass);
    }

    [Fact]
    public void EvaluateAssertion_NullActual_ReturnsFalse()
    {
        OpenClawHttpExecutionStrategy.EvaluateAssertion(null, "equals", "anything").ShouldBeFalse();
    }

    [Fact]
    public void EvaluateAssertion_UnknownOperator_DefaultsToEquals()
    {
        OpenClawHttpExecutionStrategy.EvaluateAssertion("a", "unknown_op", "a").ShouldBeTrue();
        OpenClawHttpExecutionStrategy.EvaluateAssertion("a", "unknown_op", "b").ShouldBeFalse();
    }
}
