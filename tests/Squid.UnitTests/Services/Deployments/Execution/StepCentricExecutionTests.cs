using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Squid.Core.Services.DeploymentExecution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.DeploymentExecution.Variables;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class StepCentricExecutionTests
{
    // ========== FindMatchingTargetsForStep ==========

    [Fact]
    public void FindMatchingTargets_StepWithRoles_ReturnsOnlyMatchingTargets()
    {
        var step = MakeStep("web-server");
        var targets = new List<DeploymentTargetContext>
        {
            MakeTarget("web-server"),
            MakeTarget("api-server"),
            MakeTarget("db-server")
        };

        var result = TargetStepMatcher.FindMatchingTargetsForStep(step, targets);

        result.Count.ShouldBe(1);
        result[0].Machine.Roles.ShouldBe(JsonSerializer.Serialize(new[] { "web-server" }));
    }

    [Fact]
    public void FindMatchingTargets_StepWithNoRoles_ReturnsAllTargets()
    {
        var step = MakeStep(targetRoles: null);
        var targets = new List<DeploymentTargetContext>
        {
            MakeTarget("web-server"),
            MakeTarget("api-server")
        };

        var result = TargetStepMatcher.FindMatchingTargetsForStep(step, targets);

        result.Count.ShouldBe(2);
    }

    [Fact]
    public void FindMatchingTargets_StepMultipleRoles_ReturnsUnion()
    {
        var step = MakeStep("web-server,api-server");
        var targets = new List<DeploymentTargetContext>
        {
            MakeTarget("web-server"),
            MakeTarget("api-server"),
            MakeTarget("web-server", "api-server"),
            MakeTarget("db-server")
        };

        var result = TargetStepMatcher.FindMatchingTargetsForStep(step, targets);

        result.Count.ShouldBe(3);
        var dbRolesJson = JsonSerializer.Serialize(new[] { "db-server" });
        result.ShouldNotContain(t => t.Machine.Roles == dbRolesJson);
    }

    [Fact]
    public void FindMatchingTargets_NoMatchingTargets_ReturnsEmpty()
    {
        var step = MakeStep("cache-server");
        var targets = new List<DeploymentTargetContext>
        {
            MakeTarget("web-server"),
            MakeTarget("api-server")
        };

        var result = TargetStepMatcher.FindMatchingTargetsForStep(step, targets);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void FindMatchingTargets_MultiRoleMachine_MatchesAnyRole()
    {
        var step = MakeStep("api-server");
        var targets = new List<DeploymentTargetContext>
        {
            MakeTarget("web-server", "api-server")
        };

        var result = TargetStepMatcher.FindMatchingTargetsForStep(step, targets);

        result.Count.ShouldBe(1);
    }

    [Fact]
    public void FindMatchingTargets_CaseInsensitive_Matches()
    {
        var step = MakeStep("web-server");
        var targets = new List<DeploymentTargetContext>
        {
            MakeTarget("Web-Server")
        };

        var result = TargetStepMatcher.FindMatchingTargetsForStep(step, targets);

        result.Count.ShouldBe(1);
    }

    // ========== BuildEffectiveVariables ==========

    [Fact]
    public void BuildEffectiveVariables_CombinesBaseAndEndpoint()
    {
        var baseVars = new List<VariableDto>
        {
            new() { Name = "A", Value = "1" },
            new() { Name = "B", Value = "2" }
        };

        var target = new DeploymentTargetContext
        {
            EndpointVariables = new List<VariableDto>
            {
                new() { Name = "C", Value = "3" },
                new() { Name = "D", Value = "4" }
            }
        };

        var result = EffectiveVariableBuilder.BuildEffectiveVariables(baseVars, target, new VariableScopeContext());

        result.Count.ShouldBe(4);
        result.Select(v => v.Name).ShouldBe(new[] { "A", "B", "C", "D" });
    }

    [Fact]
    public void BuildEffectiveVariables_EmptyEndpoint_ReturnsBaseOnly()
    {
        var baseVars = new List<VariableDto>
        {
            new() { Name = "A", Value = "1" },
            new() { Name = "B", Value = "2" }
        };

        var target = new DeploymentTargetContext();

        var result = EffectiveVariableBuilder.BuildEffectiveVariables(baseVars, target, new VariableScopeContext());

        result.Count.ShouldBe(2);
    }

    [Fact]
    public void BuildEffectiveVariables_DoesNotMutateBase()
    {
        var baseVars = new List<VariableDto>
        {
            new() { Name = "A", Value = "1" }
        };

        var target = new DeploymentTargetContext
        {
            EndpointVariables = new List<VariableDto>
            {
                new() { Name = "B", Value = "2" }
            }
        };

        EffectiveVariableBuilder.BuildEffectiveVariables(baseVars, target, new VariableScopeContext());

        baseVars.Count.ShouldBe(1);
        baseVars[0].Name.ShouldBe("A");
    }

    // ========== Variable Isolation ==========

    [Fact]
    public void EffectiveVariables_TwoTargets_NoBleed()
    {
        var baseVars = new List<VariableDto>
        {
            new() { Name = "SharedVar", Value = "shared" }
        };

        var targetA = new DeploymentTargetContext
        {
            EndpointVariables = new List<VariableDto>
            {
                new() { Name = "EndpointUrl", Value = "https://a.example.com" }
            }
        };

        var targetB = new DeploymentTargetContext
        {
            EndpointVariables = new List<VariableDto>
            {
                new() { Name = "EndpointUrl", Value = "https://b.example.com" }
            }
        };

        var effectiveA = EffectiveVariableBuilder.BuildEffectiveVariables(baseVars, targetA, new VariableScopeContext());
        var effectiveB = EffectiveVariableBuilder.BuildEffectiveVariables(baseVars, targetB, new VariableScopeContext());

        effectiveA.ShouldNotContain(v => v.Value == "https://b.example.com");
        effectiveB.ShouldNotContain(v => v.Value == "https://a.example.com");
    }

    [Fact]
    public void EffectiveVariables_EndpointOverridesBase_LastWins()
    {
        var baseVars = new List<VariableDto>
        {
            new() { Name = "Url", Value = "base-url" }
        };

        var target = new DeploymentTargetContext
        {
            EndpointVariables = new List<VariableDto>
            {
                new() { Name = "Url", Value = "endpoint-url" }
            }
        };

        var effective = EffectiveVariableBuilder.BuildEffectiveVariables(baseVars, target, new VariableScopeContext());

        effective.Count.ShouldBe(2);
        effective.Last(v => v.Name == "Url").Value.ShouldBe("endpoint-url");
    }

    [Fact]
    public void EffectiveVariables_OutputVarsInBase_SharedAcrossTargets()
    {
        var baseVars = new List<VariableDto>
        {
            new() { Name = "SharedVar", Value = "shared" }
        };

        var targetA = new DeploymentTargetContext
        {
            EndpointVariables = new List<VariableDto>
            {
                new() { Name = "EndpointA", Value = "a" }
            }
        };

        var targetB = new DeploymentTargetContext
        {
            EndpointVariables = new List<VariableDto>
            {
                new() { Name = "EndpointB", Value = "b" }
            }
        };

        baseVars.Add(new VariableDto { Name = "OutputVar", Value = "step1-output" });

        var effectiveA = EffectiveVariableBuilder.BuildEffectiveVariables(baseVars, targetA, new VariableScopeContext());
        var effectiveB = EffectiveVariableBuilder.BuildEffectiveVariables(baseVars, targetB, new VariableScopeContext());

        effectiveA.ShouldContain(v => v.Name == "OutputVar" && v.Value == "step1-output");
        effectiveB.ShouldContain(v => v.Name == "OutputVar" && v.Value == "step1-output");
    }

    [Fact]
    public void BuildEffectiveVariables_ResultLists_AreIndependentAcrossTargets()
    {
        var baseVars = new List<VariableDto>
        {
            new() { Name = "SharedVar", Value = "shared" }
        };

        var targetA = new DeploymentTargetContext
        {
            EndpointVariables = new List<VariableDto>
            {
                new() { Name = "EndpointA", Value = "a" }
            }
        };

        var targetB = new DeploymentTargetContext
        {
            EndpointVariables = new List<VariableDto>
            {
                new() { Name = "EndpointB", Value = "b" }
            }
        };

        var effectiveA = EffectiveVariableBuilder.BuildEffectiveVariables(baseVars, targetA, new VariableScopeContext());
        var effectiveB = EffectiveVariableBuilder.BuildEffectiveVariables(baseVars, targetB, new VariableScopeContext());

        effectiveA.Add(new VariableDto { Name = "Temp", Value = "A-only" });

        effectiveA.Count.ShouldBe(3);
        effectiveB.Count.ShouldBe(2);
        baseVars.Count.ShouldBe(1);
    }

    [Fact]
    public void BuildEffectiveVariables_ReturnsNewListInstance_EachCall()
    {
        var baseVars = new List<VariableDto>
        {
            new() { Name = "SharedVar", Value = "shared" }
        };

        var target = new DeploymentTargetContext
        {
            EndpointVariables = new List<VariableDto>
            {
                new() { Name = "Endpoint", Value = "x" }
            }
        };

        var first = EffectiveVariableBuilder.BuildEffectiveVariables(baseVars, target, new VariableScopeContext());
        var second = EffectiveVariableBuilder.BuildEffectiveVariables(baseVars, target, new VariableScopeContext());

        ReferenceEquals(first, second).ShouldBeFalse();
    }

    // ========== Helpers ==========

    private static DeploymentStepDto MakeStep(string targetRoles)
    {
        var step = new DeploymentStepDto
        {
            Name = "TestStep",
            Properties = new List<DeploymentStepPropertyDto>()
        };

        if (targetRoles != null)
        {
            step.Properties.Add(new DeploymentStepPropertyDto
            {
                PropertyName = DeploymentVariables.Action.TargetRoles,
                PropertyValue = targetRoles
            });
        }

        return step;
    }

    private static DeploymentTargetContext MakeTarget(params string[] roles)
    {
        var json = JsonSerializer.Serialize(roles);
        return new DeploymentTargetContext
        {
            Machine = new Squid.Core.Persistence.Entities.Deployments.Machine
            {
                Name = $"machine-{string.Join(",", roles)}",
                Roles = json
            }
        };
    }
}
