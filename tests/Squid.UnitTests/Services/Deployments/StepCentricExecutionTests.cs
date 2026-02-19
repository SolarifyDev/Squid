using System.Collections.Generic;
using System.Linq;
using Squid.Core.Services.Deployments;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments;

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

        var result = DeploymentTaskExecutor.FindMatchingTargetsForStep(step, targets);

        result.Count.ShouldBe(1);
        result[0].Machine.Roles.ShouldBe("web-server");
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

        var result = DeploymentTaskExecutor.FindMatchingTargetsForStep(step, targets);

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
            MakeTarget("web-server,api-server"),
            MakeTarget("db-server")
        };

        var result = DeploymentTaskExecutor.FindMatchingTargetsForStep(step, targets);

        result.Count.ShouldBe(3);
        result.ShouldNotContain(t => t.Machine.Roles == "db-server");
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

        var result = DeploymentTaskExecutor.FindMatchingTargetsForStep(step, targets);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void FindMatchingTargets_MultiRoleMachine_MatchesAnyRole()
    {
        var step = MakeStep("api-server");
        var targets = new List<DeploymentTargetContext>
        {
            MakeTarget("web-server,api-server")
        };

        var result = DeploymentTaskExecutor.FindMatchingTargetsForStep(step, targets);

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

        var result = DeploymentTaskExecutor.FindMatchingTargetsForStep(step, targets);

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

        var result = DeploymentTaskExecutor.BuildEffectiveVariables(baseVars, target);

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

        var result = DeploymentTaskExecutor.BuildEffectiveVariables(baseVars, target);

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

        DeploymentTaskExecutor.BuildEffectiveVariables(baseVars, target);

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

        var effectiveA = DeploymentTaskExecutor.BuildEffectiveVariables(baseVars, targetA);
        var effectiveB = DeploymentTaskExecutor.BuildEffectiveVariables(baseVars, targetB);

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

        var effective = DeploymentTaskExecutor.BuildEffectiveVariables(baseVars, target);

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

        var effectiveA = DeploymentTaskExecutor.BuildEffectiveVariables(baseVars, targetA);
        var effectiveB = DeploymentTaskExecutor.BuildEffectiveVariables(baseVars, targetB);

        effectiveA.ShouldContain(v => v.Name == "OutputVar" && v.Value == "step1-output");
        effectiveB.ShouldContain(v => v.Name == "OutputVar" && v.Value == "step1-output");
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

    private static DeploymentTargetContext MakeTarget(string roles)
    {
        return new DeploymentTargetContext
        {
            Machine = new Squid.Core.Persistence.Entities.Deployments.Machine
            {
                Name = $"machine-{roles}",
                Roles = roles
            }
        };
    }
}
