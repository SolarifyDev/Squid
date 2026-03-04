using System.Collections.Generic;
using Squid.Core.Services.DeploymentExecution;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments;

public class VariableDictionaryFactoryTests
{
    [Fact]
    public void Create_NullList_ReturnsEmpty()
    {
        var dict = VariableDictionaryFactory.Create(null);

        dict.ShouldNotBeNull();
        dict.GetNames().Count.ShouldBe(0);
    }

    [Fact]
    public void Create_SingleVariable_HasValue()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "Env", Value = "Production" }
        };

        var dict = VariableDictionaryFactory.Create(variables);

        dict.Get("Env").ShouldBe("Production");
    }

    [Fact]
    public void Create_NullName_Skipped()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = null, Value = "val" },
            new() { Name = "Valid", Value = "ok" }
        };

        var dict = VariableDictionaryFactory.Create(variables);

        dict.GetNames().Count.ShouldBe(1);
        dict.Get("Valid").ShouldBe("ok");
    }

    [Fact]
    public void Create_NullValue_StoredAsEmpty()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "Key", Value = null }
        };

        var dict = VariableDictionaryFactory.Create(variables);

        dict.Get("Key").ShouldBe("");
    }

    [Fact]
    public void Create_MultipleVariables_AllPresent()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "A", Value = "1" },
            new() { Name = "B", Value = "2" },
            new() { Name = "C", Value = "3" }
        };

        var dict = VariableDictionaryFactory.Create(variables);

        dict.GetNames().Count.ShouldBe(3);
        dict.Get("A").ShouldBe("1");
        dict.Get("B").ShouldBe("2");
        dict.Get("C").ShouldBe("3");
    }
}
