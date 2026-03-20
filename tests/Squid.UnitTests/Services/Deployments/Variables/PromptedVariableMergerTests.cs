using System.Collections.Generic;
using Shouldly;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Variables;

public class PromptedVariableMergerTests
{
    [Fact]
    public void MergePromptedValues_OverridesPromptedVariable()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "DbHost", Value = "default-host", PromptLabel = "Database Host" }
        };

        PromptedVariableMerger.MergePromptedValues(variables, new Dictionary<string, string> { ["DbHost"] = "prod-host" });

        variables[0].Value.ShouldBe("prod-host");
    }

    [Fact]
    public void MergePromptedValues_LeavesNonPromptedUntouched()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "AppName", Value = "Squid" }
        };

        PromptedVariableMerger.MergePromptedValues(variables, new Dictionary<string, string> { ["AppName"] = "Override" });

        variables[0].Value.ShouldBe("Squid");
    }

    [Fact]
    public void MergePromptedValues_EmptyFormValues_NoChanges()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "DbHost", Value = "default", PromptLabel = "Database Host" }
        };

        PromptedVariableMerger.MergePromptedValues(variables, new Dictionary<string, string>());

        variables[0].Value.ShouldBe("default");
    }

    [Fact]
    public void ValidateRequiredPrompts_AllProvided_NoErrors()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "DbHost", PromptLabel = "Database Host", PromptRequired = true }
        };

        var errors = PromptedVariableMerger.ValidateRequiredPrompts(variables, new Dictionary<string, string> { ["DbHost"] = "prod-host" });

        errors.ShouldBeEmpty();
    }

    [Fact]
    public void ValidateRequiredPrompts_MissingRequired_ReturnsError()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "DbHost", PromptLabel = "Database Host", PromptRequired = true }
        };

        var errors = PromptedVariableMerger.ValidateRequiredPrompts(variables, new Dictionary<string, string>());

        errors.Count.ShouldBe(1);
        errors[0].ShouldContain("Database Host");
    }

    [Fact]
    public void ValidateRequiredPrompts_OptionalMissing_NoErrors()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "OptionalVar", PromptLabel = "Optional", PromptRequired = false }
        };

        var errors = PromptedVariableMerger.ValidateRequiredPrompts(variables, new Dictionary<string, string>());

        errors.ShouldBeEmpty();
    }

    [Fact]
    public void ValidateRequiredPrompts_RequiredWithDefaultValue_NoErrors()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "DbHost", Value = "default-host", PromptLabel = "Database Host", PromptRequired = true }
        };

        var errors = PromptedVariableMerger.ValidateRequiredPrompts(variables, new Dictionary<string, string>());

        errors.ShouldBeEmpty();
    }
}
