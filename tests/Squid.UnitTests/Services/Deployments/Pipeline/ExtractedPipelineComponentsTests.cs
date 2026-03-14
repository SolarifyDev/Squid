using System;
using System.Collections.Generic;
using System.Linq;
using Squid.Core.Services.DeploymentExecution;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Handlers;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

public class ExtractedPipelineComponentsTests
{
    [Fact]
    public void StepEligibilityEvaluator_DisabledStep_ReturnsFalse()
    {
        var step = new DeploymentStepDto { IsDisabled = true };

        var result = StepEligibilityEvaluator.ShouldExecuteStep(
            step,
            targetRoles: null,
            previousStepSucceeded: true);

        result.ShouldBeFalse();
    }

    [Fact]
    public void TargetStepMatcher_WithTargetRoles_FiltersTargets()
    {
        var step = new DeploymentStepDto
        {
            Properties = new List<DeploymentStepPropertyDto>
            {
                new()
                {
                    PropertyName = DeploymentVariables.Action.TargetRoles,
                    PropertyValue = "web"
                }
            }
        };

        var targets = new List<DeploymentTargetContext>
        {
            new() { Machine = new Squid.Core.Persistence.Entities.Deployments.Machine { Roles = "[\"web\"]" } },
            new() { Machine = new Squid.Core.Persistence.Entities.Deployments.Machine { Roles = "[\"api\"]" } }
        };

        var result = TargetStepMatcher.FindMatchingTargetsForStep(step, targets);

        result.Count.ShouldBe(1);
        result[0].Machine.Roles.ShouldBe("[\"web\"]");
    }

    [Fact]
    public void EffectiveVariableBuilder_BuildActionVariables_AddsSelectedPackageVersion()
    {
        var effectiveVariables = new List<VariableDto>
        {
            new() { Name = "Base", Value = "1" }
        };

        var action = new DeploymentActionDto { Name = "Deploy Web" };
        var selectedPackages = new List<Squid.Core.Persistence.Entities.Deployments.ReleaseSelectedPackage>
        {
            new() { ActionName = "Deploy Web", Version = "1.2.3" }
        };

        var result = EffectiveVariableBuilder.BuildActionVariables(effectiveVariables, action, selectedPackages);

        result.Count.ShouldBe(2);
        result.ShouldContain(v => v.Name == SpecialVariables.Action.PackageVersion && v.Value == "1.2.3");
    }

    [Fact]
    public void ProcessSnapshotStepConverter_ConvertsAndSortsSteps()
    {
        var snapshot = new DeploymentProcessSnapshotDto
        {
            Id = 100,
            Data = new DeploymentProcessSnapshotDataDto
            {
                StepSnapshots = new List<DeploymentStepSnapshotDataDto>
                {
                    MakeStepSnapshot(2, "Second"),
                    MakeStepSnapshot(1, "First")
                }
            }
        };

        var result = ProcessSnapshotStepConverter.Convert(snapshot);

        result.Select(s => s.Name).ShouldBe(new[] { "First", "Second" });
        result[0].ProcessId.ShouldBe(100);
    }

    private static DeploymentStepSnapshotDataDto MakeStepSnapshot(int order, string name)
    {
        return new DeploymentStepSnapshotDataDto
        {
            Id = order,
            Name = name,
            StepOrder = order,
            StepType = "Action",
            Condition = "Success",
            IsRequired = true,
            IsDisabled = false,
            CreatedDate = DateTimeOffset.UtcNow,
            Properties = new Dictionary<string, string>(),
            ActionSnapshots = new List<DeploymentActionSnapshotDataDto>
            {
                new()
                {
                    Id = order * 10,
                    Name = $"Action-{order}",
                    ActionType = "Octopus.Script",
                    ActionOrder = 1,
                    Properties = new Dictionary<string, string>()
                }
            }
        };
    }
}
