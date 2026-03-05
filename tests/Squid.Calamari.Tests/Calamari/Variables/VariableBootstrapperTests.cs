using Squid.Calamari.Variables;

namespace Squid.Calamari.Tests.Calamari.Variables;

public class VariableBootstrapperTests
{
    [Fact]
    public void GeneratePreamble_ValidVariable_ExportsCorrectly()
    {
        var vars = new Dictionary<string, string> { ["APP_NAME"] = "squid" };

        var preamble = VariableBootstrapper.GeneratePreamble(vars);

        preamble.ShouldContain("export APP_NAME=\"squid\"");
    }

    [Theory]
    [InlineData("Squid.Action[Deploy].Name")]
    [InlineData("Squid.Step[1].Status")]
    [InlineData("var[0]")]
    public void GeneratePreamble_VariableWithBrackets_IsSkipped(string name)
    {
        var vars = new Dictionary<string, string> { [name] = "value" };

        var preamble = VariableBootstrapper.GeneratePreamble(vars);

        preamble.ShouldNotContain("export");
        preamble.ShouldNotContain("value");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void GeneratePreamble_EmptyOrNullName_IsSkipped(string name)
    {
        var vars = new List<KeyValuePair<string, string>> { new(name ?? "", "value") };

        var preamble = VariableBootstrapper.GeneratePreamble(vars);

        preamble.ShouldNotContain("export");
    }

    [Fact]
    public void GeneratePreamble_NameStartingWithDigit_IsSkipped()
    {
        var vars = new Dictionary<string, string> { ["0invalid"] = "value" };

        var preamble = VariableBootstrapper.GeneratePreamble(vars);

        preamble.ShouldNotContain("export");
    }

    [Theory]
    [InlineData("Squid.Action.Name", "Squid_Action_Name")]
    [InlineData("my-var", "my_var")]
    [InlineData("path/to/var", "path_to_var")]
    public void GeneratePreamble_SanitizesDotsHyphenSlashes(string name, string expectedEnvName)
    {
        var vars = new Dictionary<string, string> { [name] = "test" };

        var preamble = VariableBootstrapper.GeneratePreamble(vars);

        preamble.ShouldContain($"export {expectedEnvName}=\"test\"");
    }

    [Theory]
    [InlineData("hello\"world", "hello\\\"world")]
    [InlineData("price$5", "price\\$5")]
    [InlineData("back\\slash", "back\\\\slash")]
    [InlineData("tick`cmd`", "tick\\`cmd\\`")]
    public void GeneratePreamble_EscapesSpecialCharsInValue(string value, string expectedEscaped)
    {
        var vars = new Dictionary<string, string> { ["VAR"] = value };

        var preamble = VariableBootstrapper.GeneratePreamble(vars);

        preamble.ShouldContain($"export VAR=\"{expectedEscaped}\"");
    }

    [Theory]
    [InlineData("line1\nline2", "line1\\nline2")]
    [InlineData("col1\tcol2", "col1\\tcol2")]
    [InlineData("win\r\nline", "win\\r\\nline")]
    public void GeneratePreamble_EscapesNewlinesAndTabs(string value, string expectedEscaped)
    {
        var vars = new Dictionary<string, string> { ["VAR"] = value };

        var preamble = VariableBootstrapper.GeneratePreamble(vars);

        preamble.ShouldContain($"export VAR=\"{expectedEscaped}\"");
    }

    [Fact]
    public void GeneratePreamble_MixedValidAndInvalid_OnlyExportsValid()
    {
        var vars = new Dictionary<string, string>
        {
            ["VALID_VAR"] = "good",
            ["Squid.Action[0].Name"] = "bad",
            ["another_valid"] = "also_good"
        };

        var preamble = VariableBootstrapper.GeneratePreamble(vars);

        preamble.ShouldContain("export VALID_VAR=\"good\"");
        preamble.ShouldContain("export another_valid=\"also_good\"");
        preamble.ShouldNotContain("[0]");
    }
}
