using System.Text;
using Squid.Core.Services.DeploymentExecution.Variables;

namespace Squid.UnitTests.Services.Deployments.Variables;

public class YamlStructuredVariableReplacerTests
{
    private static byte[] YamlBytes(string yaml) => Encoding.UTF8.GetBytes(yaml);

    private static string ResultYaml(byte[] result) => Encoding.UTF8.GetString(result);

    private static Dictionary<string, string> Vars(params (string Key, string Value)[] pairs)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs)
            dict[key] = value;
        return dict;
    }

    [Fact]
    public void Replace_SimpleScalar()
    {
        var content = YamlBytes("key: old\n");
        var replacements = Vars(("key", "new"));

        var result = YamlStructuredVariableReplacer.ReplaceInYamlFile(content, replacements);

        ResultYaml(result).ShouldContain("key: new");
    }

    [Fact]
    public void Replace_NestedPath()
    {
        var content = YamlBytes("parent:\n  child: old\n");
        var replacements = Vars(("parent:child", "new"));

        var result = YamlStructuredVariableReplacer.ReplaceInYamlFile(content, replacements);

        ResultYaml(result).ShouldContain("child: new");
    }

    [Fact]
    public void Replace_DeeplyNested()
    {
        var content = YamlBytes("l1:\n  l2:\n    l3:\n      l4: old\n");
        var replacements = Vars(("l1:l2:l3:l4", "deep"));

        var result = YamlStructuredVariableReplacer.ReplaceInYamlFile(content, replacements);

        ResultYaml(result).ShouldContain("l4: deep");
    }

    [Fact]
    public void Replace_SequenceByIndex()
    {
        var content = YamlBytes("items:\n- a\n- b\n");
        var replacements = Vars(("items:0", "x"));

        var result = YamlStructuredVariableReplacer.ReplaceInYamlFile(content, replacements);

        var yaml = ResultYaml(result);
        yaml.ShouldContain("x");
        yaml.ShouldContain("b");
    }

    [Fact]
    public void Replace_ObjectInSequence()
    {
        var content = YamlBytes("items:\n- name: old\n  value: keep\n");
        var replacements = Vars(("items:0:name", "new"));

        var result = YamlStructuredVariableReplacer.ReplaceInYamlFile(content, replacements);

        var yaml = ResultYaml(result);
        yaml.ShouldContain("name: new");
        yaml.ShouldContain("value: keep");
    }

    [Fact]
    public void Replace_MultipleDocuments()
    {
        var content = YamlBytes("---\nkey1: old1\n---\nkey1: old2\n");
        var replacements = Vars(("key1", "new"));

        var result = YamlStructuredVariableReplacer.ReplaceInYamlFile(content, replacements);

        var yaml = ResultYaml(result);
        yaml.ShouldNotContain("old1");
        yaml.ShouldNotContain("old2");
        yaml.ShouldContain("key1: new");
    }

    [Fact]
    public void Replace_MultipleVariables()
    {
        var content = YamlBytes("host: old-host\nport: 80\n");
        var replacements = Vars(("host", "new-host"), ("port", "443"));

        var result = YamlStructuredVariableReplacer.ReplaceInYamlFile(content, replacements);

        var yaml = ResultYaml(result);
        yaml.ShouldContain("host: new-host");
        yaml.ShouldContain("port: 443");
    }

    [Fact]
    public void Replace_NoMatch_Unchanged()
    {
        var content = YamlBytes("key: value\n");
        var replacements = Vars(("other", "new"));

        var result = YamlStructuredVariableReplacer.ReplaceInYamlFile(content, replacements);

        ResultYaml(result).ShouldContain("key: value");
    }

    [Fact]
    public void Replace_CaseInsensitive()
    {
        var content = YamlBytes("ConnectionString: old\n");
        var replacements = Vars(("connectionstring", "new"));

        var result = YamlStructuredVariableReplacer.ReplaceInYamlFile(content, replacements);

        ResultYaml(result).ShouldContain("ConnectionString: new");
    }

    [Fact]
    public void Replace_EmptyContent_ReturnsEmpty()
    {
        var result = YamlStructuredVariableReplacer.ReplaceInYamlFile(Array.Empty<byte>(), Vars(("K", "V")));

        result.ShouldBe(Array.Empty<byte>());
    }

    [Fact]
    public void Replace_PreservesKeys()
    {
        var content = YamlBytes("parent:\n  child: old\n  other: keep\n");
        var replacements = Vars(("parent:child", "new"));

        var result = YamlStructuredVariableReplacer.ReplaceInYamlFile(content, replacements);

        var yaml = ResultYaml(result);
        yaml.ShouldContain("child: new");
        yaml.ShouldContain("other: keep");
    }
}
