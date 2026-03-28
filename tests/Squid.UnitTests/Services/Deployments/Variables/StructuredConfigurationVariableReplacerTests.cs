using System.Text;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.VariableSubstitution;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Variables;

public class StructuredConfigurationVariableReplacerTests
{
    private static VariableDictionary MakeDict(params (string Name, string Value)[] vars)
    {
        var list = new List<VariableDto>();
        foreach (var (name, value) in vars)
            list.Add(new VariableDto { Name = name, Value = value });
        return VariableDictionaryFactory.Create(list);
    }

    private static ActionExecutionResult MakeResult(bool enabled, Dictionary<string, byte[]> files = null)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (enabled)
            props[SpecialVariables.Action.StructuredConfigurationVariablesEnabled] = "True";

        return new ActionExecutionResult
        {
            ActionProperties = props,
            Files = files ?? new Dictionary<string, byte[]>()
        };
    }

    [Fact]
    public void ReplaceIfEnabled_FeatureNotSet_FilesUnchanged()
    {
        var files = new Dictionary<string, byte[]> { ["config.json"] = Encoding.UTF8.GetBytes("""{"Key":"old"}""") };
        var prepared = new ActionExecutionResult { ActionProperties = new Dictionary<string, string>(), Files = files };
        var dict = MakeDict(("Key", "new"));

        StructuredConfigurationVariableReplacer.ReplaceIfEnabled(prepared, dict);

        Encoding.UTF8.GetString(prepared.Files["config.json"]).ShouldContain("old");
    }

    [Fact]
    public void ReplaceIfEnabled_FeatureDisabled_FilesUnchanged()
    {
        var files = new Dictionary<string, byte[]> { ["config.json"] = Encoding.UTF8.GetBytes("""{"Key":"old"}""") };
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SpecialVariables.Action.StructuredConfigurationVariablesEnabled] = "False"
        };
        var prepared = new ActionExecutionResult { ActionProperties = props, Files = files };
        var dict = MakeDict(("Key", "new"));

        StructuredConfigurationVariableReplacer.ReplaceIfEnabled(prepared, dict);

        Encoding.UTF8.GetString(prepared.Files["config.json"]).ShouldContain("old");
    }

    [Fact]
    public void ReplaceIfEnabled_FeatureEnabled_ReplacesMatches()
    {
        var files = new Dictionary<string, byte[]> { ["config.json"] = Encoding.UTF8.GetBytes("""{"Key":"old"}""") };
        var prepared = MakeResult(true, files);
        var dict = MakeDict(("Key", "new"));

        StructuredConfigurationVariableReplacer.ReplaceIfEnabled(prepared, dict);

        Encoding.UTF8.GetString(prepared.Files["config.json"]).ShouldContain("\"new\"");
    }

    [Fact]
    public void ReplaceIfEnabled_CaseInsensitiveFlag_Replaces()
    {
        var files = new Dictionary<string, byte[]> { ["config.json"] = Encoding.UTF8.GetBytes("""{"Key":"old"}""") };
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SpecialVariables.Action.StructuredConfigurationVariablesEnabled] = "true"
        };
        var prepared = new ActionExecutionResult { ActionProperties = props, Files = files };
        var dict = MakeDict(("Key", "new"));

        StructuredConfigurationVariableReplacer.ReplaceIfEnabled(prepared, dict);

        Encoding.UTF8.GetString(prepared.Files["config.json"]).ShouldContain("\"new\"");
    }

    [Fact]
    public void ReplaceIfEnabled_NullFiles_NoError()
    {
        var prepared = MakeResult(true);
        prepared.Files = null;
        var dict = MakeDict(("Key", "new"));

        Should.NotThrow(() => StructuredConfigurationVariableReplacer.ReplaceIfEnabled(prepared, dict));
    }

    [Fact]
    public void ReplaceIfEnabled_EmptyFiles_NoError()
    {
        var prepared = MakeResult(true);
        var dict = MakeDict(("Key", "new"));

        Should.NotThrow(() => StructuredConfigurationVariableReplacer.ReplaceIfEnabled(prepared, dict));
    }

    [Fact]
    public void ReplaceIfEnabled_MixedFileTypes_OnlyStructuredProcessed()
    {
        var jsonContent = Encoding.UTF8.GetBytes("""{"Key":"old"}""");
        var yamlContent = Encoding.UTF8.GetBytes("Key: old\n");
        var shellContent = Encoding.UTF8.GetBytes("echo Key=old");
        var files = new Dictionary<string, byte[]>
        {
            ["config.json"] = jsonContent,
            ["values.yaml"] = yamlContent,
            ["deploy.sh"] = shellContent
        };
        var prepared = MakeResult(true, files);
        var dict = MakeDict(("Key", "new"));

        StructuredConfigurationVariableReplacer.ReplaceIfEnabled(prepared, dict);

        Encoding.UTF8.GetString(prepared.Files["config.json"]).ShouldContain("\"new\"");
        Encoding.UTF8.GetString(prepared.Files["values.yaml"]).ShouldContain("Key: new");
        Encoding.UTF8.GetString(prepared.Files["deploy.sh"]).ShouldBe("echo Key=old");
    }

    [Fact]
    public void ReplaceIfEnabled_NullActionProperties_NoError()
    {
        var prepared = new ActionExecutionResult { ActionProperties = null, Files = new Dictionary<string, byte[]>() };
        var dict = MakeDict(("Key", "new"));

        Should.NotThrow(() => StructuredConfigurationVariableReplacer.ReplaceIfEnabled(prepared, dict));
    }

    [Fact]
    public void BuildReplacementMap_FiltersReservedVariables()
    {
        var dict = MakeDict(
            ("Squid.Machine.Name", "m1"),
            ("System.TeamProject", "proj"),
            ("UserVar", "val"));

        var map = StructuredConfigurationVariableReplacer.BuildReplacementMap(dict);

        map.ShouldNotContainKey("Squid.Machine.Name");
        map.ShouldNotContainKey("System.TeamProject");
        map.ShouldContainKeyAndValue("UserVar", "val");
    }

    [Fact]
    public void BuildReplacementMap_IncludesColonPrefixedVariables()
    {
        var dict = MakeDict(("Squid:Setting", "val"), ("ConnectionStrings:Database", "Server=prod"));

        var map = StructuredConfigurationVariableReplacer.BuildReplacementMap(dict);

        map.ShouldContainKeyAndValue("Squid:Setting", "val");
        map.ShouldContainKeyAndValue("ConnectionStrings:Database", "Server=prod");
    }

    [Fact]
    public void ExistingOctostache_StillWorks_WhenFeatureDisabled()
    {
        var jsonContent = Encoding.UTF8.GetBytes("""{"Key":"#{Var}"}""");
        var files = new Dictionary<string, byte[]> { ["config.json"] = jsonContent };
        var prepared = new ActionExecutionResult { ActionProperties = new Dictionary<string, string>(), Files = files };
        var dict = MakeDict(("Var", "replaced"));

        StructuredConfigurationVariableReplacer.ReplaceIfEnabled(prepared, dict);

        // File should NOT be touched when feature is disabled — #{Var} remains
        Encoding.UTF8.GetString(prepared.Files["config.json"]).ShouldContain("#{Var}");
    }

    [Fact]
    public void FeatureDisabled_ByDefault()
    {
        var result = StructuredConfigurationVariableReplacer.IsEnabled(new Dictionary<string, string>());

        result.ShouldBeFalse();
    }
}
