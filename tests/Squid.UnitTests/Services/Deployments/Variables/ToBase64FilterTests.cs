using System.Text;
using Squid.Core.VariableSubstitution;

namespace Squid.UnitTests.Services.Deployments.Variables;

/// <summary>
/// Octostache's <c>ToBase64</c> filter (UTF-8). Imported Octopus scripts carry deploy-time values as
/// <c>#{Name | ToBase64}</c>, so a missing variable must stay an unresolved token (never "" or a crash).
/// </summary>
public class ToBase64FilterTests
{
    [Theory]
    [InlineData("hello")]
    [InlineData("")]
    [InlineData("x\n'@\nSQUID_OCTOPUS_VALUE\r\n")]
    [InlineData("你好 ✓ 🚀")]
    [InlineData("it's \"q\" $(id) `id`")]
    public void ToBase64_EncodesTheUtf8Value(string value)
    {
        var dictionary = new VariableDictionary();
        dictionary.Set("Note", value);

        dictionary.Evaluate("#{Note | ToBase64}").ShouldBe(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)));
    }

    [Fact]
    public void ToBase64_EvaluatesNestedReferencesInTheValueFirst()
    {
        var dictionary = new VariableDictionary();
        dictionary.Set("Base", "https://hook");
        dictionary.Set("Url", "#{Base}/send");

        dictionary.Evaluate("#{Url | toBASE64}").ShouldBe(Convert.ToBase64String(Encoding.UTF8.GetBytes("https://hook/send")));
    }

    [Theory]
    [InlineData("#{Missing | ToBase64}", "#{Missing | ToBase64}")]
    [InlineData("#{Note | ToBase64 utf16}", "#{Note | ToBase64 utf16}")]
    public void ToBase64_MissingValueOrUnsupportedOption_LeavesTheTokenUnresolved(string template, string expected)
    {
        var dictionary = new VariableDictionary();
        dictionary.Set("Note", "x");

        dictionary.Evaluate(template).ShouldBe(expected);
    }

    [Fact]
    public void ToBase64_ChainsAfterOtherFilters()
    {
        var dictionary = new VariableDictionary();
        dictionary.Set("Note", "abc");

        dictionary.Evaluate("#{Note | ToUpper | ToBase64}").ShouldBe(Convert.ToBase64String(Encoding.UTF8.GetBytes("ABC")));
    }
}
