using System.Linq;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.DeploymentExecution.Planning;
using Squid.Message.Enums;

namespace Squid.UnitTests.Services.Deployments.Preview;

/// <summary>
/// Phase 6c-ii — unit tests for <see cref="DeploymentService.MapPlannedStep"/>. The preview
/// service now delegates step/target/action resolution to <see cref="IDeploymentPlanner"/>
/// and only owns the translation from <see cref="PlannedStep"/> to
/// <see cref="Squid.Message.Models.Deployments.Deployment.DeploymentPreviewStepResult"/>.
/// Structural planner coverage lives in <c>DeploymentPlannerTests</c>; these tests pin the
/// mapping contract only.
/// </summary>
public class DeploymentPreviewStepTests
{
    [Fact]
    public void MapPlannedStep_ApplicableStep_CopiesIdNameOrderAndMatchedTargets()
    {
        var planned = new PlannedStep
        {
            StepId = 10,
            StepName = "Deploy Web",
            StepOrder = 1,
            Status = PlannedStepStatus.Applicable,
            RequiredRoles = new List<string> { "web" },
            MatchedTargets = new List<PlannedTarget>
            {
                new() { MachineId = 1, MachineName = "web-1", Roles = new List<string> { "web" }, CommunicationStyle = CommunicationStyle.KubernetesApi },
                new() { MachineId = 2, MachineName = "web-2", Roles = new List<string> { "web" }, CommunicationStyle = CommunicationStyle.KubernetesApi }
            },
            Actions = new List<PlannedAction>
            {
                new() { ActionId = 100, ActionName = "Run", ActionType = "Squid.Script", ActionOrder = 1 }
            }
        };

        var result = DeploymentService.MapPlannedStep(planned);

        result.StepId.ShouldBe(10);
        result.StepName.ShouldBe("Deploy Web");
        result.StepOrder.ShouldBe(1);
        result.IsApplicable.ShouldBeTrue();
        result.IsDisabled.ShouldBeFalse();
        result.IsStepLevelOnly.ShouldBeFalse();
        result.IsRunOnServer.ShouldBeFalse();
        result.Reason.ShouldBeNull();
        result.RequiredRoles.ShouldBe(new[] { "web" });
        result.RunnableActionIds.ShouldBe(new[] { 100 });
        result.MatchedTargets.Count.ShouldBe(2);
        result.MatchedTargets.Select(t => t.MachineName).ShouldBe(new[] { "web-1", "web-2" });
    }

    [Fact]
    public void MapPlannedStep_StepLevelOnly_HasEmptyTargetsAndNoReason()
    {
        var planned = new PlannedStep
        {
            StepId = 1,
            StepName = "Approve",
            StepOrder = 1,
            Status = PlannedStepStatus.StepLevelOnly,
            StatusMessage = "Step \"Approve\" runs at step-level (no per-target dispatches).",
            Actions = new List<PlannedAction>
            {
                new() { ActionId = 10, ActionName = "Gate", ActionType = "Squid.Manual", ActionOrder = 1, IsStepLevel = true }
            }
        };

        var result = DeploymentService.MapPlannedStep(planned);

        result.IsApplicable.ShouldBeTrue();
        result.IsStepLevelOnly.ShouldBeTrue();
        result.IsRunOnServer.ShouldBeFalse();
        result.MatchedTargets.ShouldBeEmpty();
        result.Reason.ShouldBeNull();
    }

    [Fact]
    public void MapPlannedStep_RunOnServer_HasEmptyTargetsAndNoReason()
    {
        var planned = new PlannedStep
        {
            StepId = 1,
            StepName = "Server Script",
            StepOrder = 1,
            Status = PlannedStepStatus.RunOnServer,
            StatusMessage = "Step \"Server Script\" is marked RunOnServer.",
            Actions = new List<PlannedAction>
            {
                new() { ActionId = 10, ActionName = "Run", ActionType = "Squid.Script", ActionOrder = 1 }
            }
        };

        var result = DeploymentService.MapPlannedStep(planned);

        result.IsApplicable.ShouldBeTrue();
        result.IsRunOnServer.ShouldBeTrue();
        result.IsStepLevelOnly.ShouldBeFalse();
        result.MatchedTargets.ShouldBeEmpty();
        result.Reason.ShouldBeNull();
    }

    [Fact]
    public void MapPlannedStep_Disabled_IsNotApplicableAndCarriesReason()
    {
        var planned = new PlannedStep
        {
            StepId = 1,
            StepName = "Disabled Step",
            StepOrder = 5,
            Status = PlannedStepStatus.Disabled,
            StatusMessage = "Step \"Disabled Step\" is disabled."
        };

        var result = DeploymentService.MapPlannedStep(planned);

        result.IsDisabled.ShouldBeTrue();
        result.IsApplicable.ShouldBeFalse();
        result.Reason.ShouldBe("Step \"Disabled Step\" is disabled.");
        result.StepOrder.ShouldBe(5);
    }

    [Fact]
    public void MapPlannedStep_NoRunnableActions_IsNotApplicableAndCarriesReason()
    {
        var planned = new PlannedStep
        {
            StepId = 1,
            StepName = "Deploy",
            StepOrder = 1,
            Status = PlannedStepStatus.NoRunnableActions,
            StatusMessage = "Step \"Deploy\" has no runnable actions for the selected environment/channel."
        };

        var result = DeploymentService.MapPlannedStep(planned);

        result.IsApplicable.ShouldBeFalse();
        result.IsDisabled.ShouldBeFalse();
        result.Reason.ShouldBe("Step \"Deploy\" has no runnable actions for the selected environment/channel.");
    }

    [Fact]
    public void MapPlannedStep_NoMatchingTargets_IsApplicableButCarriesReasonAndEmptyTargets()
    {
        var planned = new PlannedStep
        {
            StepId = 1,
            StepName = "Deploy Web",
            StepOrder = 1,
            Status = PlannedStepStatus.NoMatchingTargets,
            StatusMessage = "Step \"Deploy Web\" requires roles [web] but no candidate target has any of them.",
            RequiredRoles = new List<string> { "web" }
        };

        var result = DeploymentService.MapPlannedStep(planned);

        result.IsApplicable.ShouldBeTrue();
        result.MatchedTargets.ShouldBeEmpty();
        result.Reason.ShouldBe("Step \"Deploy Web\" requires roles [web] but no candidate target has any of them.");
        result.RequiredRoles.ShouldBe(new[] { "web" });
    }

    [Fact]
    public void MapPlannedStep_CopiesRunnableActionIds()
    {
        var planned = new PlannedStep
        {
            StepId = 1,
            StepName = "Deploy",
            StepOrder = 1,
            Status = PlannedStepStatus.Applicable,
            Actions = new List<PlannedAction>
            {
                new() { ActionId = 100, ActionName = "Step1", ActionType = "Squid.Script", ActionOrder = 1 },
                new() { ActionId = 200, ActionName = "Step2", ActionType = "Squid.Script", ActionOrder = 2 }
            }
        };

        var result = DeploymentService.MapPlannedStep(planned);

        result.RunnableActionIds.ShouldBe(new[] { 100, 200 });
    }
}
