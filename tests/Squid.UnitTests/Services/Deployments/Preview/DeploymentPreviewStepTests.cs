using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Environments;
using Squid.Core.Services.Deployments.LifeCycle;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.Core.Services.Deployments.Validation;
using Squid.Core.Services.Identity;
using Squid.Core.Services.Jobs;
using Squid.Core.Services.Machines;
using Squid.Message.Models.Deployments.Deployment;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Preview;

public class DeploymentPreviewStepTests
{
    private readonly Mock<IActionHandlerRegistry> _registryMock = new();
    private readonly DeploymentService _sut;

    public DeploymentPreviewStepTests()
    {
        _sut = new DeploymentService(
            new Mock<IMapper>().Object,
            new Mock<ICurrentUser>().Object,
            new Mock<IDeploymentDataProvider>().Object,
            new Mock<IReleaseDataProvider>().Object,
            new Mock<IEnvironmentDataProvider>().Object,
            new Mock<IMachineDataProvider>().Object,
            new Mock<ILifecycleResolver>().Object,
            new Mock<ILifecycleProgressionEvaluator>().Object,
            new Mock<IDeploymentValidationOrchestrator>().Object,
            new Mock<IDeploymentSnapshotService>().Object,
            new Mock<IServerTaskDataProvider>().Object,
            new Mock<IServerTaskService>().Object,
            _registryMock.Object,
            new Mock<ISquidBackgroundJobClient>().Object);
    }

    private static DeploymentValidationContext DefaultContext(int environmentId = 1, int skipActionId = 0)
    {
        return new DeploymentValidationContext
        {
            ReleaseId = 1,
            EnvironmentId = environmentId,
            SkipActionIds = skipActionId > 0 ? [skipActionId] : []
        };
    }

    private static List<Machine> CreateMachines(params (int id, string name, string roles)[] defs)
    {
        return defs.Select(d => new Machine { Id = d.id, Name = d.name, Roles = d.roles }).ToList();
    }

    private static DeploymentStepDto CreateStep(int id, int stepOrder, string name, bool isDisabled, string targetRoles, List<DeploymentActionDto> actions)
    {
        var properties = new List<DeploymentStepPropertyDto>();

        if (!string.IsNullOrEmpty(targetRoles))
        {
            properties.Add(new DeploymentStepPropertyDto
            {
                StepId = id,
                PropertyName = DeploymentVariables.Action.TargetRoles,
                PropertyValue = targetRoles
            });
        }

        return new DeploymentStepDto
        {
            Id = id,
            StepOrder = stepOrder,
            Name = name,
            IsDisabled = isDisabled,
            Properties = properties,
            Actions = actions
        };
    }

    private static DeploymentActionDto CreateAction(int id, string actionType, int actionOrder = 1)
    {
        return new DeploymentActionDto
        {
            Id = id,
            ActionType = actionType,
            ActionOrder = actionOrder,
            Environments = [],
            ExcludedEnvironments = [],
            Channels = []
        };
    }

    [Fact]
    public void ManualInterventionStep_ShouldHaveEmptyTargetsAndBeApplicable()
    {
        var action = CreateAction(10, "Squid.Manual");
        var step = CreateStep(1, 1, "Approve", false, null, [action]);
        var machines = CreateMachines((1, "k8s-node-1", "web-server"), (2, "k8s-node-2", "web-server"));

        _registryMock.Setup(r => r.ResolveScope(action)).Returns(ExecutionScope.StepLevel);

        var result = _sut.BuildStepPreview(step, 1, 0, DefaultContext(), machines);

        result.IsApplicable.ShouldBeTrue();
        result.IsStepLevelOnly.ShouldBeTrue();
        result.MatchedTargets.ShouldBeEmpty();
        result.Reason.ShouldBeNull();
    }

    [Fact]
    public void MixedStep_ManualInterventionAndTargetLevel_ShouldMatchTargetsNormally()
    {
        var manualAction = CreateAction(10, "Squid.Manual", 1);
        var scriptAction = CreateAction(11, "Squid.KubernetesRunScript", 2);
        var step = CreateStep(1, 1, "Mixed Step", false, "web-server", [manualAction, scriptAction]);
        var machines = CreateMachines((1, "k8s-node-1", "web-server"), (2, "k8s-node-2", "db-server"));

        _registryMock.Setup(r => r.ResolveScope(manualAction)).Returns(ExecutionScope.StepLevel);
        _registryMock.Setup(r => r.ResolveScope(scriptAction)).Returns(ExecutionScope.TargetLevel);

        var result = _sut.BuildStepPreview(step, 1, 0, DefaultContext(), machines);

        result.IsApplicable.ShouldBeTrue();
        result.IsStepLevelOnly.ShouldBeFalse();
        result.MatchedTargets.Count.ShouldBe(1);
        result.MatchedTargets[0].MachineName.ShouldBe("k8s-node-1");
    }

    [Fact]
    public void NormalStepWithRoles_ShouldFilterByRoles()
    {
        var action = CreateAction(10, "Squid.KubernetesRunScript");
        var step = CreateStep(1, 1, "Deploy", false, "web-server", [action]);
        var machines = CreateMachines((1, "k8s-node-1", "web-server"), (2, "k8s-node-2", "db-server"));

        _registryMock.Setup(r => r.ResolveScope(action)).Returns(ExecutionScope.TargetLevel);

        var result = _sut.BuildStepPreview(step, 1, 0, DefaultContext(), machines);

        result.IsApplicable.ShouldBeTrue();
        result.IsStepLevelOnly.ShouldBeFalse();
        result.MatchedTargets.Count.ShouldBe(1);
        result.MatchedTargets[0].MachineName.ShouldBe("k8s-node-1");
        result.RequiredRoles.ShouldContain("web-server");
    }

    [Fact]
    public void NormalStepWithoutRoles_ShouldMatchAllMachines()
    {
        var action = CreateAction(10, "Squid.KubernetesRunScript");
        var step = CreateStep(1, 1, "Deploy", false, null, [action]);
        var machines = CreateMachines((1, "k8s-node-1", "web-server"), (2, "k8s-node-2", "db-server"));

        _registryMock.Setup(r => r.ResolveScope(action)).Returns(ExecutionScope.TargetLevel);

        var result = _sut.BuildStepPreview(step, 1, 0, DefaultContext(), machines);

        result.IsApplicable.ShouldBeTrue();
        result.MatchedTargets.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void StepNumbering_ShouldUseStepOrderDirectly(int stepOrder)
    {
        var action = CreateAction(10, "Squid.KubernetesRunScript");
        var step = CreateStep(1, stepOrder, "Deploy", false, null, [action]);
        var machines = CreateMachines((1, "k8s-node-1", "web-server"));

        _registryMock.Setup(r => r.ResolveScope(action)).Returns(ExecutionScope.TargetLevel);

        var result = _sut.BuildStepPreview(step, step.StepOrder, 0, DefaultContext(), machines);

        result.StepOrder.ShouldBe(stepOrder);
    }

    [Fact]
    public void DisabledStep_ShouldGetCorrectDisplayOrder()
    {
        var step = CreateStep(1, 5, "Disabled Step", true, null, []);

        var result = _sut.BuildStepPreview(step, 2, 0, DefaultContext(), []);

        result.StepOrder.ShouldBe(2);
        result.IsDisabled.ShouldBeTrue();
        result.IsApplicable.ShouldBeFalse();
        result.Reason.ShouldBe("Step is disabled.");
    }

    [Fact]
    public void NoRunnableActions_ShouldNotBeApplicable()
    {
        var action = CreateAction(10, "Squid.KubernetesRunScript");
        action.IsDisabled = true;
        var step = CreateStep(1, 1, "Deploy", false, null, [action]);

        var result = _sut.BuildStepPreview(step, 1, 0, DefaultContext(), []);

        result.IsApplicable.ShouldBeFalse();
        result.Reason.ShouldContain("No runnable actions");
    }
}
