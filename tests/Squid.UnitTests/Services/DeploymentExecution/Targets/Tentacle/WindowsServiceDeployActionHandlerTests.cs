using System.Linq;
using Shouldly;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Tentacle.Handlers;
using Squid.Core.Services.DeploymentExecution.Validation;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

public class WindowsServiceDeployActionHandlerTests
{
    [Fact]
    public void ActionType_MatchesPublishedConstant()
    {
        new WindowsServiceDeployActionHandler().ActionType.ShouldBe("Squid.DeployWindowsService");
        new WindowsServiceDeployActionHandler().ActionType.ShouldBe(SpecialVariables.ActionTypes.DeployWindowsService);
    }

    [Fact]
    public void StaticRequirements_RequireWindowsAndPowerShell()
    {
        var requirements = new WindowsServiceDeployActionHandler().StaticRequirements;

        requirements.Keys.ShouldBe(new[] { CapabilityKeys.OsSlot, CapabilityKeys.Shell.PowerShell }, ignoreOrder: true);
        requirements[CapabilityKeys.OsSlot].ShouldContain(CapabilityKeys.Os.Windows);
        requirements[CapabilityKeys.Shell.PowerShell].ShouldContain(CapabilityKeys.Present);
    }

    [Fact]
    public async Task DescribeIntentAsync_ReturnsPowerShellRunScriptIntent_WithStableShape()
    {
        var handler = (IActionHandler)new WindowsServiceDeployActionHandler();
        var ctx = BuildContext(
            BuildAction((WindowsServiceDeployProperties.ServiceName, "OrderWorker")),
            stepName: "Deploy worker",
            actionName: "Windows Service");

        var intent = await handler.DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ShouldBeOfType<RunScriptIntent>();
        var runScript = (RunScriptIntent)intent;

        runScript.Name.ShouldBe("deploy-windows-service");
        runScript.StepName.ShouldBe("Deploy worker");
        runScript.ActionName.ShouldBe("Windows Service");
        runScript.Syntax.ShouldBe(ScriptSyntax.PowerShell);
        runScript.InjectRuntimeBundle.ShouldBeFalse();
        runScript.ScriptBody.ShouldContain("$SquidParameters['Squid.Action.WindowsService.ServiceName'] = 'OrderWorker'");
        runScript.ScriptBody.ShouldContain("function Resolve-PackageRoot");
        runScript.ScriptBody.ShouldContain("Invoke-Sc create $serviceName");
    }

    [Theory]
    [InlineData("Linux")]
    [InlineData("Darwin")]
    [InlineData("FreeBSD")]
    public async Task DescribeIntentAsync_NonWindowsTentacle_ThrowsWithActionableMessage(string os)
    {
        var handler = (IActionHandler)new WindowsServiceDeployActionHandler();
        var ctx = BuildContext(
            action: BuildAction((WindowsServiceDeployProperties.ServiceName, "OrderWorker")),
            stepName: "Deploy worker",
            actionName: "Windows Service",
            tentacleOs: os);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => handler.DescribeIntentAsync(ctx, CancellationToken.None));

        ex.Message.ShouldContain("Squid.DeployWindowsService");
        ex.Message.ShouldContain("Deploy worker");
        ex.Message.ShouldContain("Windows Service");
        ex.Message.ShouldContain(os);
        ex.Message.ShouldContain("Windows Tentacle");
        ex.Message.ShouldContain("health check");
    }

    [Theory]
    [InlineData("Windows")]
    [InlineData("windows")]
    [InlineData("WINDOWS")]
    [InlineData("Microsoft Windows NT 10.0.19045.0")]
    [InlineData("Microsoft Windows NT 10.0.22631.0")]
    [InlineData("Microsoft Windows NT 10.0.17763.0")]
    [InlineData("Microsoft Windows NT 10.0.20348.0")]
    public async Task DescribeIntentAsync_WindowsTentacle_ProceedsSuccessfully(string os)
    {
        var handler = (IActionHandler)new WindowsServiceDeployActionHandler();
        var ctx = BuildContext(
            BuildAction((WindowsServiceDeployProperties.ServiceName, "OrderWorker")),
            tentacleOs: os);

        var intent = await handler.DescribeIntentAsync(ctx, CancellationToken.None);
        intent.ShouldBeOfType<RunScriptIntent>();
    }

    [Fact]
    public async Task DescribeIntentAsync_OsCacheMiss_ProceedsOptimistically()
    {
        var handler = (IActionHandler)new WindowsServiceDeployActionHandler();
        var ctx = BuildContext(
            BuildAction((WindowsServiceDeployProperties.ServiceName, "OrderWorker")),
            tentacleOs: null);

        var intent = await handler.DescribeIntentAsync(ctx, CancellationToken.None);
        intent.ShouldBeOfType<RunScriptIntent>();
    }

    [Theory]
    [InlineData("Windows", true)]
    [InlineData("Microsoft Windows NT 10.0.19045.0", true)]
    [InlineData("Linux", false)]
    [InlineData("macOS", false)]
    [InlineData("Darwin", false)]
    [InlineData("LinuxOnWindowsSubsystem", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LooksLikeWindowsOsString_PinnedBehaviour(string osValue, bool expected)
    {
        WindowsServiceDeployActionHandler.LooksLikeWindowsOsString(osValue).ShouldBe(expected);
    }

    private static DeploymentActionDto BuildAction(params (string Name, string Value)[] properties)
    {
        return new DeploymentActionDto
        {
            Id = 1,
            Name = "Windows Service",
            ActionType = SpecialVariables.ActionTypes.DeployWindowsService,
            Properties = properties
                .Select(p => new DeploymentActionPropertyDto { PropertyName = p.Name, PropertyValue = p.Value })
                .ToList()
        };
    }

    private static ActionExecutionContext BuildContext(
        DeploymentActionDto action,
        string stepName = "Deploy",
        string actionName = "Windows Service",
        string tentacleOs = "Windows")
    {
        var variables = new List<VariableDto>();

        if (tentacleOs != null)
            variables.Add(new VariableDto { Name = WindowsServiceDeployProperties.TentacleOS, Value = tentacleOs });

        return new ActionExecutionContext
        {
            Step = new DeploymentStepDto { Name = stepName },
            Action = new DeploymentActionDto
            {
                Id = action.Id,
                Name = actionName,
                ActionType = action.ActionType,
                Properties = action.Properties
            },
            Variables = variables
        };
    }
}
