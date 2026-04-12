using Moq;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Handlers;

/// <summary>
/// Phase 9g — verifies that <see cref="ManualInterventionActionHandler"/> overrides
/// <c>DescribeIntentAsync</c> and emits a <see cref="ManualInterventionIntent"/> directly,
/// with a stable semantic name (<c>manual-intervention</c>) and the legacy
/// <c>Squid.Action.Manual.Instructions</c> + <c>Squid.Action.Manual.ResponsibleTeamIds</c>
/// properties mapped onto <see cref="ManualInterventionIntent.Instructions"/> and
/// <see cref="ManualInterventionIntent.ResponsibleTeamIds"/> respectively. The legacy
/// <c>PrepareAsync</c>/<c>ExecuteStepLevelAsync</c> path is preserved because manual
/// intervention is step-level: the interruption/suspend flow stays in the handler and
/// the intent is a marker for the renderer/UI layer.
/// </summary>
public class ManualInterventionActionHandlerDescribeIntentTests
{
    private readonly ManualInterventionActionHandler _handler;

    public ManualInterventionActionHandlerDescribeIntentTests()
    {
        var interruption = new Mock<IDeploymentInterruptionService>();
        var serverTask = new Mock<IServerTaskService>();
        var lifecycle = new Mock<IDeploymentLifecycle>();
        _handler = new ManualInterventionActionHandler(interruption.Object, serverTask.Object, lifecycle.Object);
    }

    private static DeploymentActionDto CreateAction(string actionName = "Approve Release", Dictionary<string, string> properties = null)
    {
        var action = new DeploymentActionDto
        {
            Name = actionName,
            ActionType = SpecialVariables.ActionTypes.Manual,
            Properties = new List<DeploymentActionPropertyDto>()
        };

        if (properties == null) return action;

        foreach (var kvp in properties)
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = kvp.Key,
                PropertyValue = kvp.Value
            });

        return action;
    }

    private static ActionExecutionContext CreateContext(string stepName = "Manual Step", DeploymentActionDto action = null) => new()
    {
        Step = new DeploymentStepDto { Name = stepName },
        Action = action ?? CreateAction()
    };

    // ----- Shape -----

    [Fact]
    public async Task DescribeIntentAsync_ReturnsManualInterventionIntent()
    {
        var ctx = CreateContext();

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ShouldBeOfType<ManualInterventionIntent>();
    }

    [Fact]
    public async Task DescribeIntentAsync_NameIsManualIntervention()
    {
        var ctx = CreateContext();

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Name.ShouldBe("manual-intervention");
    }

    [Fact]
    public async Task DescribeIntentAsync_NameIsNotLegacyPrefixed()
    {
        var ctx = CreateContext();

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Name.ShouldNotStartWith("legacy:");
    }

    [Fact]
    public async Task DescribeIntentAsync_PropagatesStepAndActionName()
    {
        var ctx = CreateContext("Release Approval", CreateAction("Approve by Release Manager"));

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.StepName.ShouldBe("Release Approval");
        intent.ActionName.ShouldBe("Approve by Release Manager");
    }

    // ----- Instructions -----

    [Fact]
    public async Task DescribeIntentAsync_CopiesInstructionsFromActionProperties()
    {
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            [SpecialVariables.Action.ManualInstructions] = "Please verify the staging deploy before approving."
        }));

        var intent = (ManualInterventionIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Instructions.ShouldBe("Please verify the staging deploy before approving.");
    }

    [Fact]
    public async Task DescribeIntentAsync_MissingInstructions_DefaultsToEmptyString()
    {
        var ctx = CreateContext();

        var intent = (ManualInterventionIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Instructions.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task DescribeIntentAsync_EmptyInstructionsProperty_IsEmptyString()
    {
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            [SpecialVariables.Action.ManualInstructions] = ""
        }));

        var intent = (ManualInterventionIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Instructions.ShouldBe(string.Empty);
    }

    // ----- ResponsibleTeamIds -----

    [Fact]
    public async Task DescribeIntentAsync_CopiesResponsibleTeamIds_WhenProvided()
    {
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            [SpecialVariables.Action.ManualResponsibleTeamIds] = "team-releases,team-sre"
        }));

        var intent = (ManualInterventionIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ResponsibleTeamIds.ShouldBe("team-releases,team-sre");
    }

    [Fact]
    public async Task DescribeIntentAsync_MissingResponsibleTeamIds_IsNull()
    {
        var ctx = CreateContext();

        var intent = (ManualInterventionIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ResponsibleTeamIds.ShouldBeNull();
    }

    [Fact]
    public async Task DescribeIntentAsync_EmptyResponsibleTeamIdsProperty_PreservesEmptyString()
    {
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            [SpecialVariables.Action.ManualResponsibleTeamIds] = ""
        }));

        var intent = (ManualInterventionIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ResponsibleTeamIds.ShouldBe(string.Empty);
    }

    // ----- Combined -----

    [Fact]
    public async Task DescribeIntentAsync_CopiesBothInstructionsAndResponsibleTeamIds()
    {
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            [SpecialVariables.Action.ManualInstructions] = "Review and approve",
            [SpecialVariables.Action.ManualResponsibleTeamIds] = "team-releases"
        }));

        var intent = (ManualInterventionIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Instructions.ShouldBe("Review and approve");
        intent.ResponsibleTeamIds.ShouldBe("team-releases");
    }

    // ----- Null guard -----

    [Fact]
    public async Task DescribeIntentAsync_NullContext_Throws()
    {
        await Should.ThrowAsync<ArgumentNullException>(
            async () => await ((IActionHandler)_handler).DescribeIntentAsync(null!, CancellationToken.None));
    }
}
