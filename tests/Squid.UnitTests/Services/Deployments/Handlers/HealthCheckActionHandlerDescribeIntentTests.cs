using Moq;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Handlers;

/// <summary>
/// Phase 9e — verifies that <see cref="HealthCheckActionHandler"/> overrides
/// <c>DescribeIntentAsync</c> and emits a <see cref="HealthCheckIntent"/> directly, with a
/// stable semantic name (<c>health-check</c>) and every legacy HealthCheck property mapped
/// onto a semantic intent field (<see cref="HealthCheckIntent.CheckType"/>,
/// <see cref="HealthCheckIntent.ErrorHandling"/>, <see cref="HealthCheckIntent.IncludeNewTargets"/>).
/// The legacy <c>PrepareAsync</c>/<c>ExecuteStepLevelAsync</c> path is preserved until
/// Phase 9g flips the pipeline across.
/// </summary>
public class HealthCheckActionHandlerDescribeIntentTests
{
    private readonly HealthCheckActionHandler _handler;

    public HealthCheckActionHandlerDescribeIntentTests()
    {
        var lifecycle = new Mock<IDeploymentLifecycle>();
        var targetFinder = new Mock<IDeploymentTargetFinder>();
        _handler = new HealthCheckActionHandler(lifecycle.Object, targetFinder.Object);
    }

    private static DeploymentActionDto CreateAction(
        string actionName = "run-health-check",
        Dictionary<string, string> properties = null)
    {
        var action = new DeploymentActionDto
        {
            Name = actionName,
            ActionType = SpecialVariables.ActionTypes.HealthCheck,
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

    private static ActionExecutionContext CreateContext(
        string stepName = "Check Targets",
        DeploymentActionDto action = null) => new()
    {
        Step = new DeploymentStepDto { Name = stepName },
        Action = action ?? CreateAction()
    };

    // ----- Shape -----

    [Fact]
    public async Task DescribeIntentAsync_ReturnsHealthCheckIntent()
    {
        var ctx = CreateContext();

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ShouldBeOfType<HealthCheckIntent>();
    }

    [Fact]
    public async Task DescribeIntentAsync_NameIsHealthCheck()
    {
        var ctx = CreateContext();

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Name.ShouldBe("health-check");
    }

    [Fact]
    public async Task DescribeIntentAsync_DoesNotUseLegacyName()
    {
        var ctx = CreateContext();

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Name.ShouldNotStartWith("legacy:");
    }

    [Fact]
    public async Task DescribeIntentAsync_PopulatesStepAndActionName()
    {
        var ctx = CreateContext(
            stepName: "Health Step",
            action: CreateAction(actionName: "Ping Targets"));

        var intent = (HealthCheckIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.StepName.ShouldBe("Health Step");
        intent.ActionName.ShouldBe("Ping Targets");
    }

    // ----- CheckType -----

    [Fact]
    public async Task DescribeIntentAsync_CheckType_DefaultsToFullHealthCheck()
    {
        var ctx = CreateContext();

        var intent = (HealthCheckIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.CheckType.ShouldBe(HealthCheckType.FullHealthCheck);
    }

    [Fact]
    public async Task DescribeIntentAsync_CheckType_ConnectionTestFromProperty()
    {
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.HealthCheck.Type"] = "ConnectionTest"
        }));

        var intent = (HealthCheckIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.CheckType.ShouldBe(HealthCheckType.ConnectionTest);
    }

    [Fact]
    public async Task DescribeIntentAsync_CheckType_FullHealthCheckExplicit()
    {
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.HealthCheck.Type"] = "FullHealthCheck"
        }));

        var intent = (HealthCheckIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.CheckType.ShouldBe(HealthCheckType.FullHealthCheck);
    }

    [Fact]
    public async Task DescribeIntentAsync_CheckType_UnknownValueFallsBackToFullHealthCheck()
    {
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.HealthCheck.Type"] = "SomethingElse"
        }));

        var intent = (HealthCheckIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.CheckType.ShouldBe(HealthCheckType.FullHealthCheck);
    }

    // ----- ErrorHandling -----

    [Fact]
    public async Task DescribeIntentAsync_ErrorHandling_DefaultsToFailDeployment()
    {
        var ctx = CreateContext();

        var intent = (HealthCheckIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ErrorHandling.ShouldBe(HealthCheckErrorHandling.FailDeployment);
    }

    [Fact]
    public async Task DescribeIntentAsync_ErrorHandling_TreatExceptionsAsWarningsMapsToSkipUnavailable()
    {
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.HealthCheck.ErrorHandling"] = "TreatExceptionsAsWarnings"
        }));

        var intent = (HealthCheckIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ErrorHandling.ShouldBe(HealthCheckErrorHandling.SkipUnavailable);
    }

    [Fact]
    public async Task DescribeIntentAsync_ErrorHandling_TreatExceptionsAsErrorsMapsToFailDeployment()
    {
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.HealthCheck.ErrorHandling"] = "TreatExceptionsAsErrors"
        }));

        var intent = (HealthCheckIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ErrorHandling.ShouldBe(HealthCheckErrorHandling.FailDeployment);
    }

    // ----- IncludeNewTargets -----

    [Fact]
    public async Task DescribeIntentAsync_IncludeNewTargets_DefaultsToFalse()
    {
        var ctx = CreateContext();

        var intent = (HealthCheckIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.IncludeNewTargets.ShouldBeFalse();
    }

    [Fact]
    public async Task DescribeIntentAsync_IncludeNewTargets_IncludeCheckedMachinesMapsToTrue()
    {
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.HealthCheck.IncludeMachinesInDeployment"] = "IncludeCheckedMachines"
        }));

        var intent = (HealthCheckIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.IncludeNewTargets.ShouldBeTrue();
    }

    [Fact]
    public async Task DescribeIntentAsync_IncludeNewTargets_DoNotAlterMachinesMapsToFalse()
    {
        var ctx = CreateContext(action: CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.HealthCheck.IncludeMachinesInDeployment"] = "DoNotAlterMachines"
        }));

        var intent = (HealthCheckIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.IncludeNewTargets.ShouldBeFalse();
    }

    // ----- CustomScript & Syntax -----

    [Fact]
    public async Task DescribeIntentAsync_CustomScript_DefaultsToNull()
    {
        var ctx = CreateContext();

        var intent = (HealthCheckIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.CustomScript.ShouldBeNull();
    }

    [Fact]
    public async Task DescribeIntentAsync_Syntax_DefaultsToBash()
    {
        var ctx = CreateContext();

        var intent = (HealthCheckIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Syntax.ShouldBe(ScriptSyntax.Bash);
    }

    // ----- Combined -----

    [Fact]
    public async Task DescribeIntentAsync_AllPropertiesPopulated_MapsEverything()
    {
        var ctx = CreateContext(
            stepName: "Pre-deploy health",
            action: CreateAction(
                actionName: "Check node reachability",
                properties: new Dictionary<string, string>
                {
                    ["Squid.Action.HealthCheck.Type"] = "ConnectionTest",
                    ["Squid.Action.HealthCheck.ErrorHandling"] = "TreatExceptionsAsWarnings",
                    ["Squid.Action.HealthCheck.IncludeMachinesInDeployment"] = "IncludeCheckedMachines"
                }));

        var intent = (HealthCheckIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Name.ShouldBe("health-check");
        intent.StepName.ShouldBe("Pre-deploy health");
        intent.ActionName.ShouldBe("Check node reachability");
        intent.CheckType.ShouldBe(HealthCheckType.ConnectionTest);
        intent.ErrorHandling.ShouldBe(HealthCheckErrorHandling.SkipUnavailable);
        intent.IncludeNewTargets.ShouldBeTrue();
        intent.CustomScript.ShouldBeNull();
        intent.Syntax.ShouldBe(ScriptSyntax.Bash);
    }

    // ----- Null guards -----

    [Fact]
    public async Task DescribeIntentAsync_NullContext_Throws()
    {
        await Should.ThrowAsync<ArgumentNullException>(() =>
            ((IActionHandler)_handler).DescribeIntentAsync(null, CancellationToken.None));
    }
}
