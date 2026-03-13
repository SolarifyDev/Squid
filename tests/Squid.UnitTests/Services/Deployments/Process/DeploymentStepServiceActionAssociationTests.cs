using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Process.Action;
using Squid.Core.Services.Deployments.Process.Step;
using Squid.Message.Commands.Deployments.Process.Step;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Process;

public class DeploymentStepServiceActionAssociationTests
{
    // ========== Create: Environments Persisted ==========

    [Fact]
    public async Task CreateStep_WithActionEnvironments_PersistsToTable()
    {
        var (service, mocks) = CreateServiceWithMocks();

        var command = BuildCreateCommand(environments: new List<int> { 5, 10 });

        await service.CreateDeploymentStepAsync(command, CancellationToken.None);

        mocks.ActionEnvironment.Verify(p => p.AddActionEnvironmentsAsync(It.Is<List<ActionEnvironment>>(list => list.Count == 2 && list.Any(e => e.EnvironmentId == 5) && list.Any(e => e.EnvironmentId == 10)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateStep_WithNoEnvironments_DoesNotPersist()
    {
        var (service, mocks) = CreateServiceWithMocks();

        var command = BuildCreateCommand(environments: new List<int>());

        await service.CreateDeploymentStepAsync(command, CancellationToken.None);

        mocks.ActionEnvironment.Verify(p => p.AddActionEnvironmentsAsync(It.IsAny<List<ActionEnvironment>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ========== Create: ExcludedEnvironments Persisted ==========

    [Fact]
    public async Task CreateStep_WithExcludedEnvironments_PersistsToTable()
    {
        var (service, mocks) = CreateServiceWithMocks();

        var command = BuildCreateCommand(excludedEnvironments: new List<int> { 99 });

        await service.CreateDeploymentStepAsync(command, CancellationToken.None);

        mocks.ActionExcludedEnvironment.Verify(p => p.AddActionExcludedEnvironmentsAsync(It.Is<List<ActionExcludedEnvironment>>(list => list.Count == 1 && list[0].EnvironmentId == 99), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ========== Create: Channels Persisted ==========

    [Fact]
    public async Task CreateStep_WithActionChannels_PersistsToTable()
    {
        var (service, mocks) = CreateServiceWithMocks();

        var command = BuildCreateCommand(channels: new List<int> { 20, 30 });

        await service.CreateDeploymentStepAsync(command, CancellationToken.None);

        mocks.ActionChannel.Verify(p => p.AddActionChannelsAsync(It.Is<List<ActionChannel>>(list => list.Count == 2 && list.Any(c => c.ChannelId == 20)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateStep_WithNoChannels_DoesNotPersist()
    {
        var (service, mocks) = CreateServiceWithMocks();

        var command = BuildCreateCommand(channels: new List<int>());

        await service.CreateDeploymentStepAsync(command, CancellationToken.None);

        mocks.ActionChannel.Verify(p => p.AddActionChannelsAsync(It.IsAny<List<ActionChannel>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ========== Read: Associations Loaded Into DTO ==========

    [Fact]
    public async Task GetStep_LoadsEnvironmentsIntoActionDto()
    {
        var (service, mocks) = CreateServiceWithMocks();

        mocks.ActionEnvironment.Setup(p => p.GetActionEnvironmentsByActionIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ActionEnvironment> { new() { ActionId = 1, EnvironmentId = 5 }, new() { ActionId = 1, EnvironmentId = 10 } });

        var result = await service.GetDeploymentStepByIdAsync(1, CancellationToken.None);

        result.Data.Actions[0].Environments.ShouldBe(new List<int> { 5, 10 });
    }

    [Fact]
    public async Task GetStep_LoadsExcludedEnvironmentsIntoActionDto()
    {
        var (service, mocks) = CreateServiceWithMocks();

        mocks.ActionExcludedEnvironment.Setup(p => p.GetActionExcludedEnvironmentsByActionIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ActionExcludedEnvironment> { new() { ActionId = 1, EnvironmentId = 99 } });

        var result = await service.GetDeploymentStepByIdAsync(1, CancellationToken.None);

        result.Data.Actions[0].ExcludedEnvironments.ShouldBe(new List<int> { 99 });
    }

    [Fact]
    public async Task GetStep_LoadsChannelsIntoActionDto()
    {
        var (service, mocks) = CreateServiceWithMocks();

        mocks.ActionChannel.Setup(p => p.GetActionChannelsByActionIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ActionChannel> { new() { ActionId = 1, ChannelId = 20 } });

        var result = await service.GetDeploymentStepByIdAsync(1, CancellationToken.None);

        result.Data.Actions[0].Channels.ShouldBe(new List<int> { 20 });
    }

    // ========== Combined: All Associations ==========

    [Fact]
    public async Task CreateStep_WithAllAssociations_PersistsAll()
    {
        var (service, mocks) = CreateServiceWithMocks();

        var command = BuildCreateCommand(environments: new List<int> { 1 }, excludedEnvironments: new List<int> { 2 }, channels: new List<int> { 3 });

        await service.CreateDeploymentStepAsync(command, CancellationToken.None);

        mocks.ActionEnvironment.Verify(p => p.AddActionEnvironmentsAsync(It.Is<List<ActionEnvironment>>(list => list.Count == 1), It.IsAny<CancellationToken>()), Times.Once);
        mocks.ActionExcludedEnvironment.Verify(p => p.AddActionExcludedEnvironmentsAsync(It.Is<List<ActionExcludedEnvironment>>(list => list.Count == 1), It.IsAny<CancellationToken>()), Times.Once);
        mocks.ActionChannel.Verify(p => p.AddActionChannelsAsync(It.Is<List<ActionChannel>>(list => list.Count == 1), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ========== Test Infrastructure ==========

    private record MockProviders(
        Mock<IDeploymentStepDataProvider> Step,
        Mock<IDeploymentStepPropertyDataProvider> StepProperty,
        Mock<IDeploymentActionDataProvider> Action,
        Mock<IDeploymentActionPropertyDataProvider> ActionProperty,
        Mock<IActionEnvironmentDataProvider> ActionEnvironment,
        Mock<IActionExcludedEnvironmentDataProvider> ActionExcludedEnvironment,
        Mock<IActionChannelDataProvider> ActionChannel);

    private static (DeploymentStepService Service, MockProviders Mocks) CreateServiceWithMocks()
    {
        var mapper = new MapperConfiguration(cfg => cfg.AddProfile<DeploymentProcessMapping>()).CreateMapper();

        var stepProvider = new Mock<IDeploymentStepDataProvider>();
        var stepPropertyProvider = new Mock<IDeploymentStepPropertyDataProvider>();
        var actionProvider = new Mock<IDeploymentActionDataProvider>();
        var actionPropertyProvider = new Mock<IDeploymentActionPropertyDataProvider>();
        var actionEnvProvider = new Mock<IActionEnvironmentDataProvider>();
        var actionExEnvProvider = new Mock<IActionExcludedEnvironmentDataProvider>();
        var actionChannelProvider = new Mock<IActionChannelDataProvider>();

        // Default: step insert assigns Id = 1
        stepProvider.Setup(p => p.AddDeploymentStepAsync(It.IsAny<DeploymentStep>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<DeploymentStep, bool, CancellationToken>((s, _, _) => s.Id = 1)
            .Returns(Task.CompletedTask);
        stepProvider.Setup(p => p.GetDeploymentStepsByProcessIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentStep>());

        // Default: action insert assigns Id = 1
        actionProvider.Setup(p => p.AddDeploymentActionAsync(It.IsAny<DeploymentAction>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<DeploymentAction, bool, CancellationToken>((a, _, _) => a.Id = 1)
            .Returns(Task.CompletedTask);

        // Default: GetStepById returns step with one action (for read tests)
        stepProvider.Setup(p => p.GetDeploymentStepByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentStep { Id = 1, Name = "Step 1", StepType = "Action", Condition = "Success" });
        actionProvider.Setup(p => p.GetDeploymentActionsByStepIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentAction> { new() { Id = 1, StepId = 1, Name = "Action 1", ActionType = "Squid.KubernetesRunScript" } });
        actionPropertyProvider.Setup(p => p.GetDeploymentActionPropertiesByActionIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentActionProperty>());
        stepPropertyProvider.Setup(p => p.GetDeploymentStepPropertiesByStepIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentStepProperty>());

        // Default: empty association lists for read
        actionEnvProvider.Setup(p => p.GetActionEnvironmentsByActionIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ActionEnvironment>());
        actionExEnvProvider.Setup(p => p.GetActionExcludedEnvironmentsByActionIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ActionExcludedEnvironment>());
        actionChannelProvider.Setup(p => p.GetActionChannelsByActionIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ActionChannel>());

        var service = new DeploymentStepService(mapper, stepProvider.Object, stepPropertyProvider.Object, actionProvider.Object, actionPropertyProvider.Object, actionEnvProvider.Object, actionExEnvProvider.Object, actionChannelProvider.Object);
        var mocks = new MockProviders(stepProvider, stepPropertyProvider, actionProvider, actionPropertyProvider, actionEnvProvider, actionExEnvProvider, actionChannelProvider);

        return (service, mocks);
    }

    private static CreateDeploymentStepCommand BuildCreateCommand(List<int> environments = null, List<int> excludedEnvironments = null, List<int> channels = null)
    {
        return new CreateDeploymentStepCommand
        {
            ProcessId = 1,
            Step = new CreateOrUpdateDeploymentStepModel
            {
                Name = "Deploy",
                StepType = "Action",
                Condition = "Success",
                Actions = new List<CreateOrUpdateDeploymentActionModel>
                {
                    new()
                    {
                        Name = "Run Script",
                        ActionType = "Squid.KubernetesRunScript",
                        Environments = environments ?? new List<int>(),
                        ExcludedEnvironments = excludedEnvironments ?? new List<int>(),
                        Channels = channels ?? new List<int>()
                    }
                }
            }
        };
    }
}
