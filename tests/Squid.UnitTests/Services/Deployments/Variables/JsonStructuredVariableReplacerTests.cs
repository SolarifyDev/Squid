using System.Text;
using System.Text.Json;
using Squid.Core.Services.DeploymentExecution.Variables;

namespace Squid.UnitTests.Services.Deployments.Variables;

public class JsonStructuredVariableReplacerTests
{
    private static byte[] JsonBytes(string json) => Encoding.UTF8.GetBytes(json);

    private static string ResultJson(byte[] result) => Encoding.UTF8.GetString(result);

    private static Dictionary<string, string> Vars(params (string Key, string Value)[] pairs)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs)
            dict[key] = value;
        return dict;
    }

    private static JsonElement ParseElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Replace_SimpleString()
    {
        var content = JsonBytes("""{"Key":"old"}""");
        var replacements = Vars(("Key", "new"));

        var result = JsonStructuredVariableReplacer.ReplaceInJsonFile(content, replacements);

        var parsed = ParseElement(ResultJson(result));
        parsed.GetProperty("Key").GetString().ShouldBe("new");
    }

    [Fact]
    public void Replace_NestedPath()
    {
        var content = JsonBytes("""{"A":{"B":"old"}}""");
        var replacements = Vars(("A:B", "new"));

        var result = JsonStructuredVariableReplacer.ReplaceInJsonFile(content, replacements);

        var parsed = ParseElement(ResultJson(result));
        parsed.GetProperty("A").GetProperty("B").GetString().ShouldBe("new");
    }

    [Fact]
    public void Replace_DeeplyNested()
    {
        var content = JsonBytes("""{"L1":{"L2":{"L3":{"L4":"old"}}}}""");
        var replacements = Vars(("L1:L2:L3:L4", "deep"));

        var result = JsonStructuredVariableReplacer.ReplaceInJsonFile(content, replacements);

        var parsed = ParseElement(ResultJson(result));
        parsed.GetProperty("L1").GetProperty("L2").GetProperty("L3").GetProperty("L4").GetString().ShouldBe("deep");
    }

    [Theory]
    [InlineData("443", 443L)]
    public void Replace_TypePreservation_Integer(string newValue, long expected)
    {
        var content = JsonBytes("""{"Port":80}""");
        var replacements = Vars(("Port", newValue));

        var result = JsonStructuredVariableReplacer.ReplaceInJsonFile(content, replacements);

        var parsed = ParseElement(ResultJson(result));
        parsed.GetProperty("Port").GetInt64().ShouldBe(expected);
    }

    [Theory]
    [InlineData("2.5", 2.5)]
    public void Replace_TypePreservation_Float(string newValue, double expected)
    {
        var content = JsonBytes("""{"Rate":1.0}""");
        var replacements = Vars(("Rate", newValue));

        var result = JsonStructuredVariableReplacer.ReplaceInJsonFile(content, replacements);

        var parsed = ParseElement(ResultJson(result));
        parsed.GetProperty("Rate").GetDouble().ShouldBe(expected);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Replace_TypePreservation_Boolean(string newValue, bool expected)
    {
        var content = JsonBytes("""{"Enabled":false}""");
        var replacements = Vars(("Enabled", newValue));

        var result = JsonStructuredVariableReplacer.ReplaceInJsonFile(content, replacements);

        var parsed = ParseElement(ResultJson(result));
        parsed.GetProperty("Enabled").GetBoolean().ShouldBe(expected);
    }

    [Fact]
    public void Replace_NonParseable_BecomesString()
    {
        var content = JsonBytes("""{"Port":80}""");
        var replacements = Vars(("Port", "http"));

        var result = JsonStructuredVariableReplacer.ReplaceInJsonFile(content, replacements);

        var parsed = ParseElement(ResultJson(result));
        parsed.GetProperty("Port").ValueKind.ShouldBe(JsonValueKind.String);
        parsed.GetProperty("Port").GetString().ShouldBe("http");
    }

    [Fact]
    public void Replace_NullValue()
    {
        var content = JsonBytes("""{"Key":null}""");
        var replacements = Vars(("Key", "value"));

        var result = JsonStructuredVariableReplacer.ReplaceInJsonFile(content, replacements);

        var parsed = ParseElement(ResultJson(result));
        parsed.GetProperty("Key").GetString().ShouldBe("value");
    }

    [Fact]
    public void Replace_ArrayByIndex()
    {
        var content = JsonBytes("""{"items":["a","b"]}""");
        var replacements = Vars(("items:0", "x"));

        var result = JsonStructuredVariableReplacer.ReplaceInJsonFile(content, replacements);

        var parsed = ParseElement(ResultJson(result));
        parsed.GetProperty("items")[0].GetString().ShouldBe("x");
        parsed.GetProperty("items")[1].GetString().ShouldBe("b");
    }

    [Fact]
    public void Replace_ObjectInArray()
    {
        var content = JsonBytes("""{"items":[{"name":"old"}]}""");
        var replacements = Vars(("items:0:name", "new"));

        var result = JsonStructuredVariableReplacer.ReplaceInJsonFile(content, replacements);

        var parsed = ParseElement(ResultJson(result));
        parsed.GetProperty("items")[0].GetProperty("name").GetString().ShouldBe("new");
    }

    [Fact]
    public void Replace_EntireObjectWithJson()
    {
        var content = JsonBytes("""{"A":{"B":"old"}}""");
        var replacements = Vars(("A:B", """{"X":1}"""));

        var result = JsonStructuredVariableReplacer.ReplaceInJsonFile(content, replacements);

        var parsed = ParseElement(ResultJson(result));
        parsed.GetProperty("A").GetProperty("B").GetProperty("X").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void Replace_MultipleVariables()
    {
        var content = JsonBytes("""{"Host":"old-host","Port":80}""");
        var replacements = Vars(("Host", "new-host"), ("Port", "443"));

        var result = JsonStructuredVariableReplacer.ReplaceInJsonFile(content, replacements);

        var parsed = ParseElement(ResultJson(result));
        parsed.GetProperty("Host").GetString().ShouldBe("new-host");
        parsed.GetProperty("Port").GetInt64().ShouldBe(443);
    }

    [Fact]
    public void Replace_NoMatch_Unchanged()
    {
        var content = JsonBytes("""{"Key":"value"}""");
        var replacements = Vars(("Other", "new"));

        var result = JsonStructuredVariableReplacer.ReplaceInJsonFile(content, replacements);

        var parsed = ParseElement(ResultJson(result));
        parsed.GetProperty("Key").GetString().ShouldBe("value");
    }

    [Fact]
    public void Replace_CaseInsensitive()
    {
        var content = JsonBytes("""{"ConnectionStrings":{"Database":"old"}}""");
        var replacements = Vars(("connectionstrings:database", "new"));

        var result = JsonStructuredVariableReplacer.ReplaceInJsonFile(content, replacements);

        var parsed = ParseElement(ResultJson(result));
        parsed.GetProperty("ConnectionStrings").GetProperty("Database").GetString().ShouldBe("new");
    }

    [Fact]
    public void Replace_EmptyContent_ReturnsEmpty()
    {
        var result = JsonStructuredVariableReplacer.ReplaceInJsonFile(Array.Empty<byte>(), Vars(("K", "V")));

        result.ShouldBe(Array.Empty<byte>());
    }

    [Fact]
    public void Replace_RootArray()
    {
        var content = JsonBytes("""[{"Name":"a"},{"Name":"b"}]""");
        var replacements = Vars(("0:Name", "replaced"));

        var result = JsonStructuredVariableReplacer.ReplaceInJsonFile(content, replacements);

        var parsed = ParseElement(ResultJson(result));
        parsed[0].GetProperty("Name").GetString().ShouldBe("replaced");
        parsed[1].GetProperty("Name").GetString().ShouldBe("b");
    }

    [Fact]
    public void Replace_PreservesStructure()
    {
        var content = JsonBytes("""{"A":{"B":"old"},"C":"keep"}""");
        var replacements = Vars(("A:B", "new"));

        var result = JsonStructuredVariableReplacer.ReplaceInJsonFile(content, replacements);

        var parsed = ParseElement(ResultJson(result));
        parsed.GetProperty("A").GetProperty("B").GetString().ShouldBe("new");
        parsed.GetProperty("C").GetString().ShouldBe("keep");
    }
}
