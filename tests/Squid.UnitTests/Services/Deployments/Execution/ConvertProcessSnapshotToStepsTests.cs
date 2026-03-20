using System;
using System.Collections.Generic;
using System.Linq;
using Squid.Core.Services.DeploymentExecution;
using Squid.Message.Models.Deployments.Snapshots;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Message.Constants;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class ConvertProcessSnapshotToStepsTests
{
    [Fact]
    public void StepIsDisabled_PreservedFromSnapshot()
    {
        var snapshot = BuildSnapshot(
            MakeStep(1, "Enabled Step", isDisabled: false, isRequired: true),
            MakeStep(2, "Disabled Step", isDisabled: true, isRequired: false));

        var steps = ProcessSnapshotStepConverter.Convert(snapshot);

        steps.Count.ShouldBe(2);
        steps[0].IsDisabled.ShouldBeFalse();
        steps[0].IsRequired.ShouldBeTrue();
        steps[1].IsDisabled.ShouldBeTrue();
        steps[1].IsRequired.ShouldBeFalse();
    }

    [Fact]
    public void ActionIsDisabledAndIsRequired_PreservedFromSnapshot()
    {
        var stepSnap = MakeStep(1, "Step");
        stepSnap.ActionSnapshots = new List<DeploymentActionSnapshotDataDto>
        {
            new() { Id = 1, Name = "Enabled", ActionType = "T", ActionOrder = 1, IsDisabled = false, IsRequired = true },
            new() { Id = 2, Name = "Disabled", ActionType = "T", ActionOrder = 2, IsDisabled = true, IsRequired = false }
        };

        var steps = ProcessSnapshotStepConverter.Convert(BuildSnapshot(stepSnap));

        steps[0].Actions[0].IsDisabled.ShouldBeFalse();
        steps[0].Actions[0].IsRequired.ShouldBeTrue();
        steps[0].Actions[1].IsDisabled.ShouldBeTrue();
        steps[0].Actions[1].IsRequired.ShouldBeFalse();
    }

    [Fact]
    public void StepAndActionProperties_CorrectlyConverted()
    {
        var stepSnap = MakeStep(1, "Step", condition: "Variable");
        stepSnap.Properties = new Dictionary<string, string> { { SpecialVariables.Step.TargetRoles, "web" } };
        stepSnap.ActionSnapshots = new List<DeploymentActionSnapshotDataDto>
        {
            new()
            {
                Id = 1, Name = "A", ActionType = "T", ActionOrder = 1,
                Properties = new Dictionary<string, string> { { "Key1", "Val1" } }
            }
        };

        var steps = ProcessSnapshotStepConverter.Convert(BuildSnapshot(stepSnap));

        steps[0].Condition.ShouldBe("Variable");
        steps[0].Properties.ShouldContain(p => p.PropertyName == SpecialVariables.Step.TargetRoles && p.PropertyValue == "web");
        steps[0].Actions[0].Properties.ShouldContain(p => p.PropertyName == "Key1" && p.PropertyValue == "Val1");
    }

    [Fact]
    public void Steps_SortedByStepOrder()
    {
        var snapshot = BuildSnapshot(
            MakeStep(3, "Third"),
            MakeStep(1, "First"),
            MakeStep(2, "Second"));

        var steps = ProcessSnapshotStepConverter.Convert(snapshot);

        steps[0].Name.ShouldBe("First");
        steps[1].Name.ShouldBe("Second");
        steps[2].Name.ShouldBe("Third");
    }

    [Fact]
    public void EmptySnapshot_ReturnsEmptyList()
    {
        var snapshot = new DeploymentProcessSnapshotDto
        {
            Id = 1, OriginalProcessId = 1, Version = 1,
            Data = new DeploymentProcessSnapshotDataDto()
        };

        var steps = ProcessSnapshotStepConverter.Convert(snapshot);

        steps.ShouldBeEmpty();
    }

    // ========== Target Roles Flow Through Snapshot ==========

    [Fact]
    public void TargetRoles_MultipleStepsWithDifferentRoles()
    {
        var step1 = MakeStep(1, "Deploy Web");
        step1.Properties = new Dictionary<string, string> { { SpecialVariables.Step.TargetRoles, "web" } };

        var step2 = MakeStep(2, "Deploy API");
        step2.Properties = new Dictionary<string, string> { { SpecialVariables.Step.TargetRoles, "api" } };

        var step3 = MakeStep(3, "Run Smoke Tests");
        // No target roles — runs on all machines

        var steps = ProcessSnapshotStepConverter.Convert(BuildSnapshot(step1, step2, step3));

        steps[0].Properties.ShouldContain(p => p.PropertyName == SpecialVariables.Step.TargetRoles && p.PropertyValue == "web");
        steps[1].Properties.ShouldContain(p => p.PropertyName == SpecialVariables.Step.TargetRoles && p.PropertyValue == "api");
        steps[2].Properties.ShouldNotContain(p => p.PropertyName == SpecialVariables.Step.TargetRoles);
    }

    [Fact]
    public void ActionEnvironmentsAndChannels_PreservedFromSnapshot()
    {
        var stepSnap = MakeStep(1, "Step");
        stepSnap.ActionSnapshots = new List<DeploymentActionSnapshotDataDto>
        {
            new()
            {
                Id = 1, Name = "A", ActionType = "T", ActionOrder = 1,
                Environments = new List<int> { 1, 2 },
                ExcludedEnvironments = new List<int> { 3 },
                Channels = new List<int> { 10, 20 }
            }
        };

        var steps = ProcessSnapshotStepConverter.Convert(BuildSnapshot(stepSnap));

        var action = steps[0].Actions[0];
        action.Environments.ShouldBe(new List<int> { 1, 2 });
        action.ExcludedEnvironments.ShouldBe(new List<int> { 3 });
        action.Channels.ShouldBe(new List<int> { 10, 20 });
    }

    [Fact]
    public void StartTrigger_PreservedFromSnapshot()
    {
        var stepSnap = MakeStep(1, "First");
        var stepSnap2 = MakeStep(2, "Second");
        stepSnap2.StartTrigger = "StartWithPrevious";

        var steps = ProcessSnapshotStepConverter.Convert(BuildSnapshot(stepSnap, stepSnap2));

        steps[0].StartTrigger.ShouldBe("");
        steps[1].StartTrigger.ShouldBe("StartWithPrevious");
    }

    [Fact]
    public void EmptyTargetRoles_PreservedAsProperty()
    {
        var stepSnap = MakeStep(1, "Step");
        stepSnap.Properties = new Dictionary<string, string>
        {
            { SpecialVariables.Step.TargetRoles, "" }
        };

        var steps = ProcessSnapshotStepConverter.Convert(BuildSnapshot(stepSnap));

        steps[0].Properties.ShouldContain(p => p.PropertyName == SpecialVariables.Step.TargetRoles && p.PropertyValue == "");
    }

    private static DeploymentStepSnapshotDataDto MakeStep(
        int stepOrder, string name,
        bool isDisabled = false, bool isRequired = true,
        string condition = "Success")
    {
        return new DeploymentStepSnapshotDataDto
        {
            Id = stepOrder,
            Name = name,
            StepType = "Action",
            StepOrder = stepOrder,
            Condition = condition,
            IsDisabled = isDisabled,
            IsRequired = isRequired,
            CreatedDate = DateTimeOffset.UtcNow,
            ActionSnapshots = new List<DeploymentActionSnapshotDataDto>
            {
                new() { Id = stepOrder * 10, Name = $"Action-{stepOrder}", ActionType = "Octopus.Script", ActionOrder = 1 }
            }
        };
    }

    private static DeploymentProcessSnapshotDto BuildSnapshot(params DeploymentStepSnapshotDataDto[] stepSnapshots)
    {
        return new DeploymentProcessSnapshotDto
        {
            Id = 1, OriginalProcessId = 1, Version = 1,
            Data = new DeploymentProcessSnapshotDataDto
            {
                StepSnapshots = stepSnapshots.ToList()
            }
        };
    }
}
