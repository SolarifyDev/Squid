using System.Collections.Generic;
using Squid.Core.Extensions;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments;

public class DeploymentActionDtoExtensionsTests
{
    [Fact]
    public void GetProperty_NullAction_ReturnsNull()
    {
        DeploymentActionDto action = null;

        action.GetProperty("SomeKey").ShouldBeNull();
    }

    [Fact]
    public void GetProperty_NullProperties_ReturnsNull()
    {
        var action = new DeploymentActionDto { Properties = null };

        action.GetProperty("SomeKey").ShouldBeNull();
    }

    [Fact]
    public void GetProperty_MissingKey_ReturnsNull()
    {
        var action = new DeploymentActionDto
        {
            Properties = new List<DeploymentActionPropertyDto>
            {
                new() { PropertyName = "OtherKey", PropertyValue = "OtherValue" }
            }
        };

        action.GetProperty("SomeKey").ShouldBeNull();
    }

    [Theory]
    [InlineData("Squid.Action.Script.ScriptBody", "Squid.Action.Script.ScriptBody")]   // exact match
    [InlineData("squid.action.script.scriptbody", "Squid.Action.Script.ScriptBody")]   // lower → upper
    [InlineData("SQUID.ACTION.SCRIPT.SCRIPTBODY", "Squid.Action.Script.ScriptBody")]   // upper → mixed
    public void GetProperty_CaseInsensitive_ReturnsValue(string lookupKey, string storedKey)
    {
        var action = new DeploymentActionDto
        {
            Properties = new List<DeploymentActionPropertyDto>
            {
                new() { PropertyName = storedKey, PropertyValue = "echo hello" }
            }
        };

        action.GetProperty(lookupKey).ShouldBe("echo hello");
    }
}
