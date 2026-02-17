using System.Collections.Generic;
using Squid.Core.Services.Deployments;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments;

public class VariableExpanderTests
{
    private static List<VariableDto> MakeVars(params (string Name, string Value)[] vars)
    {
        var list = new List<VariableDto>();
        foreach (var (name, value) in vars)
            list.Add(new VariableDto { Name = name, Value = value });
        return list;
    }

    [Fact]
    public void ExpandActionProperties_SimpleVariable_Expanded()
    {
        var dict = VariableDictionaryFactory.Create(MakeVars(("Env", "Production")));
        var action = new DeploymentActionDto
        {
            Id = 1, Name = "Test", ActionType = "Test",
            Properties = new List<DeploymentActionPropertyDto>
            {
                new() { PropertyName = "Target", PropertyValue = "#{Env}" }
            }
        };

        var expanded = VariableExpander.ExpandActionProperties(action, dict);

        expanded.Properties[0].PropertyValue.ShouldBe("Production");
    }

    [Fact]
    public void ExpandActionProperties_MissingVariable_LeftAsIs()
    {
        var dict = VariableDictionaryFactory.Create(new List<VariableDto>());
        var action = new DeploymentActionDto
        {
            Id = 1, Name = "Test", ActionType = "Test",
            Properties = new List<DeploymentActionPropertyDto>
            {
                new() { PropertyName = "Target", PropertyValue = "#{Unknown}" }
            }
        };

        var expanded = VariableExpander.ExpandActionProperties(action, dict);

        expanded.Properties[0].PropertyValue.ShouldBe("#{Unknown}");
    }

    [Fact]
    public void ExpandActionProperties_IndirectVariable_Resolved()
    {
        var dict = VariableDictionaryFactory.Create(MakeVars(
            ("Target", "#{ApiUrl}"),
            ("ApiUrl", "https://api.example.com")));
        var action = new DeploymentActionDto
        {
            Id = 1, Name = "Test", ActionType = "Test",
            Properties = new List<DeploymentActionPropertyDto>
            {
                new() { PropertyName = "Url", PropertyValue = "#{Target}" }
            }
        };

        var expanded = VariableExpander.ExpandActionProperties(action, dict);

        expanded.Properties[0].PropertyValue.ShouldBe("https://api.example.com");
    }

    [Fact]
    public void ExpandActionProperties_NullAction_NoThrow()
    {
        var dict = VariableDictionaryFactory.Create(new List<VariableDto>());

        var expanded = VariableExpander.ExpandActionProperties(null, dict);

        expanded.ShouldBeNull();
    }

    [Fact]
    public void ExpandActionProperties_EmptyPropertyValue_Unchanged()
    {
        var dict = VariableDictionaryFactory.Create(MakeVars(("Env", "Prod")));
        var action = new DeploymentActionDto
        {
            Id = 1, Name = "Test", ActionType = "Test",
            Properties = new List<DeploymentActionPropertyDto>
            {
                new() { PropertyName = "Key", PropertyValue = "" }
            }
        };

        var expanded = VariableExpander.ExpandActionProperties(action, dict);

        expanded.Properties[0].PropertyValue.ShouldBe("");
    }

    [Fact]
    public void ExpandActionProperties_DoesNotMutateOriginal()
    {
        var dict = VariableDictionaryFactory.Create(MakeVars(("Env", "Production")));
        var action = new DeploymentActionDto
        {
            Id = 1, Name = "Test", ActionType = "Test",
            Properties = new List<DeploymentActionPropertyDto>
            {
                new() { PropertyName = "Target", PropertyValue = "#{Env}" }
            }
        };

        VariableExpander.ExpandActionProperties(action, dict);

        action.Properties[0].PropertyValue.ShouldBe("#{Env}");
    }

    [Fact]
    public void ExpandString_SimpleSubstitution()
    {
        var dict = VariableDictionaryFactory.Create(MakeVars(("Name", "World")));

        var result = VariableExpander.ExpandString("Hello #{Name}!", dict);

        result.ShouldBe("Hello World!");
    }

    [Fact]
    public void ExpandString_NullInput_ReturnsNull()
    {
        var dict = VariableDictionaryFactory.Create(new List<VariableDto>());

        var result = VariableExpander.ExpandString(null, dict);

        result.ShouldBeNull();
    }

    [Fact]
    public void ExpandString_NoTokens_Unchanged()
    {
        var dict = VariableDictionaryFactory.Create(MakeVars(("X", "Y")));

        var result = VariableExpander.ExpandString("plain text", dict);

        result.ShouldBe("plain text");
    }
}
