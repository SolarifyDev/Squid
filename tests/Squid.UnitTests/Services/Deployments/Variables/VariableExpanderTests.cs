using System.Collections.Generic;
using System.Linq;
using Squid.Core.Services.DeploymentExecution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.VariableSubstitution;

namespace Squid.UnitTests.Services.Deployments.Variables;

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

    [Fact]
    public void ExpandActionProperties_AfterBuildActionVariablesPromotesProperties_StillResolvesSimpleTokens()
    {
        // Regression for IIS E2E failures after BuildActionVariables started promoting
        // action properties into the variable dictionary. Production path is:
        //   base vars + action.Properties -> BuildActionVariables
        //   -> VariableDictionaryFactory.Create -> ExpandActionProperties
        // PropertyListBinder nests dotted Squid.Action.* keys; simple tokens like
        // #{EnvName} must still resolve when those nested keys are present.
        var baseVars = MakeVars(
            ("EnvName", "Production"),
            ("CertThumbprint", "ABCDEF0123456789ABCDEF0123456789ABCDEF01"));

        var action = new DeploymentActionDto
        {
            Name = "IIS WebSite",
            Properties = new List<DeploymentActionPropertyDto>
            {
                new() { PropertyName = "Squid.Action.IISWebSite.ConfigurationTransforms.EnvironmentName", PropertyValue = "#{EnvName}" },
                new() { PropertyName = "Squid.Action.IISWebSite.Bindings", PropertyValue = "[{\"thumbprint\":\"#{CertThumbprint}\"}]" },
                new() { PropertyName = "Squid.Action.IISWebSite.WebSiteName", PropertyValue = "OrderApi" },
            }
        };

        // Production order: expand with base vars only, then promote expanded props.
        var expansionDict = VariableDictionaryFactory.Create(baseVars);
        var expanded = VariableExpander.ExpandActionProperties(action, expansionDict);
        var actionVars = EffectiveVariableBuilder.BuildActionVariables(baseVars, expanded, selectedPackages: null);

        expanded.Properties.Single(p => p.PropertyName.EndsWith("EnvironmentName")).PropertyValue.ShouldBe("Production");
        expanded.Properties.Single(p => p.PropertyName.EndsWith("Bindings")).PropertyValue.ShouldContain("ABCDEF0123456789ABCDEF0123456789ABCDEF01");
        expanded.Properties.Single(p => p.PropertyName.EndsWith("Bindings")).PropertyValue.ShouldNotContain("#{CertThumbprint}");

        // Variables shipped to the agent must not reintroduce unresolved templates.
        actionVars.ShouldContain(v => v.Name.EndsWith("EnvironmentName") && v.Value == "Production");
        actionVars.ShouldNotContain(v => (v.Value ?? string.Empty).Contains("#{"));
    }

    [Fact]
    public void ExpandActionProperties_WhenRawPropertiesPromotedBeforeExpansion_LeavesTokensIfBindingPoisoned()
    {
        // Documents the IIS E2E failure mode: promoting raw action property values
        // into the dictionary before expansion is not what breaks ExpandActionProperties
        // itself (simple tokens still resolve), but those raw values end up in
        // $SquidVariables and fail whole-script "no #{" assertions. This test pins
        // the production-safe order via the sibling test above; this one asserts that
        // BuildActionVariables on an unexpanded action still carries raw templates.
        var baseVars = MakeVars(("EnvName", "Production"));
        var action = new DeploymentActionDto
        {
            Name = "IIS WebSite",
            Properties = new List<DeploymentActionPropertyDto>
            {
                new() { PropertyName = "Squid.Action.IISWebSite.ConfigurationTransforms.EnvironmentName", PropertyValue = "#{EnvName}" },
            }
        };

        var rawPromoted = EffectiveVariableBuilder.BuildActionVariables(baseVars, action, selectedPackages: null);
        rawPromoted.ShouldContain(v => v.Name.EndsWith("EnvironmentName") && v.Value == "#{EnvName}");
    }
}
