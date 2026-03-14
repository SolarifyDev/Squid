using Squid.Core.Persistence.Db;
using Squid.Core.Services.Deployments.Process.Action;
using Squid.Core.Services.Deployments.Process.Step;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.Deployments.Process.Step;
using Squid.Message.Events.Deployments.Step;
using Squid.Message.Models.Deployments.Process;

namespace Squid.IntegrationTests.Services.Deployments.Process.Step;

public class DeploymentStepServiceTests : TestBase
{
    public DeploymentStepServiceTests()
        : base("DeploymentStepService", "squid_it_deployment_step_service")
    {
    }

    [Fact]
    public async Task CreateStep_WithProperties_StepIdNotZeroOnProperties()
    {
        var processId = await SeedProcessAsync();

        var result = await Run<IDeploymentStepService, DeploymentStepCreatedEvent>(async service =>
        {
            var command = new CreateDeploymentStepCommand
            {
                ProcessId = processId,
                Step = new CreateOrUpdateDeploymentStepModel
                {
                    Name = "Deploy Step",
                    StepType = "Action",
                    Condition = "Success",
                    StartTrigger = "",
                    PackageRequirement = "",
                    Properties =
                    [
                        new StepPropertyModel { PropertyName = "Squid.Step.TargetRoles", PropertyValue = "web-server" }
                    ]
                }
            };

            return await service.CreateDeploymentStepAsync(command, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var stepId = result.Data.Id;
        stepId.ShouldBeGreaterThan(0);

        await Run<IDeploymentStepPropertyDataProvider>(async provider =>
        {
            var persisted = await provider.GetDeploymentStepPropertiesByStepIdAsync(stepId, CancellationToken.None).ConfigureAwait(false);

            persisted.Count.ShouldBe(1);
            persisted[0].StepId.ShouldBe(stepId);
            persisted[0].PropertyName.ShouldBe("Squid.Step.TargetRoles");
            persisted[0].PropertyValue.ShouldBe("web-server");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task CreateStep_WithActions_ActionStepIdNotZero()
    {
        var processId = await SeedProcessAsync();

        var result = await Run<IDeploymentStepService, DeploymentStepCreatedEvent>(async service =>
        {
            var command = new CreateDeploymentStepCommand
            {
                ProcessId = processId,
                Step = new CreateOrUpdateDeploymentStepModel
                {
                    Name = "Deploy Step",
                    StepType = "Action",
                    Condition = "Success",
                    StartTrigger = "",
                    PackageRequirement = "",
                    Actions =
                    [
                        new CreateOrUpdateDeploymentActionModel { Name = "Deploy Containers", ActionType = "Squid.KubernetesDeployContainers" }
                    ]
                }
            };

            return await service.CreateDeploymentStepAsync(command, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var stepId = result.Data.Id;
        stepId.ShouldBeGreaterThan(0);

        await Run<IDeploymentActionDataProvider>(async provider =>
        {
            var actions = await provider.GetDeploymentActionsByStepIdAsync(stepId, CancellationToken.None).ConfigureAwait(false);

            actions.Count.ShouldBe(1);
            actions[0].StepId.ShouldBe(stepId);
            actions[0].Id.ShouldBeGreaterThan(0);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task CreateStep_WithActionsAndActionProperties_ActionIdNotZeroOnProperties()
    {
        var processId = await SeedProcessAsync();

        var result = await Run<IDeploymentStepService, DeploymentStepCreatedEvent>(async service =>
        {
            var command = new CreateDeploymentStepCommand
            {
                ProcessId = processId,
                Step = new CreateOrUpdateDeploymentStepModel
                {
                    Name = "Deploy Step",
                    StepType = "Action",
                    Condition = "Success",
                    StartTrigger = "",
                    PackageRequirement = "",
                    Actions =
                    [
                        new CreateOrUpdateDeploymentActionModel
                        {
                            Name = "Deploy Containers",
                            ActionType = "Squid.KubernetesDeployContainers",
                            Properties =
                            [
                                new ActionPropertyModel { PropertyName = "Squid.Action.KubernetesContainers.Namespace", PropertyValue = "production" },
                                new ActionPropertyModel { PropertyName = "Squid.Action.KubernetesContainers.DeploymentName", PropertyValue = "my-app" }
                            ]
                        }
                    ]
                }
            };

            return await service.CreateDeploymentStepAsync(command, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var stepId = result.Data.Id;
        var actionId = result.Data.Actions[0].Id;

        stepId.ShouldBeGreaterThan(0);
        actionId.ShouldBeGreaterThan(0);

        await Run<IDeploymentActionPropertyDataProvider>(async provider =>
        {
            var properties = await provider.GetDeploymentActionPropertiesByActionIdAsync(actionId, CancellationToken.None).ConfigureAwait(false);

            properties.Count.ShouldBe(2);
            properties.ShouldAllBe(p => p.ActionId == actionId);
            properties.ShouldAllBe(p => p.ActionId != 0);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task CreateStep_AssignsSequentialStepOrder()
    {
        var processId = await SeedProcessAsync();

        var first = await CreateStepAsync(processId, "Step One");
        var second = await CreateStepAsync(processId, "Step Two");
        var third = await CreateStepAsync(processId, "Step Three");

        first.StepOrder.ShouldBe(1);
        second.StepOrder.ShouldBe(2);
        third.StepOrder.ShouldBe(3);
    }

    [Fact]
    public async Task UpdateStep_ReplacesPropertiesAndActions()
    {
        var processId = await SeedProcessAsync();

        var created = await CreateStepAsync(processId, "Original Step",
            properties: [new StepPropertyModel { PropertyName = "OldProp", PropertyValue = "old" }],
            actions: [new CreateOrUpdateDeploymentActionModel { Name = "Old Action", ActionType = "Squid.KubernetesRunScript" }]);

        var stepId = created.Id;

        await Run<IDeploymentStepService>(async service =>
        {
            var command = new UpdateDeploymentStepCommand
            {
                Id = stepId,
                Step = new CreateOrUpdateDeploymentStepModel
                {
                    Name = "Updated Step",
                    StepType = "Action",
                    Condition = "Success",
                    StartTrigger = "",
                    PackageRequirement = "",
                    Properties =
                    [
                        new StepPropertyModel { PropertyName = "NewProp", PropertyValue = "new" }
                    ],
                    Actions =
                    [
                        new CreateOrUpdateDeploymentActionModel { Name = "New Action", ActionType = "Squid.KubernetesDeployContainers" }
                    ]
                }
            };

            await service.UpdateDeploymentStepAsync(command, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IDeploymentStepPropertyDataProvider>(async provider =>
        {
            var properties = await provider.GetDeploymentStepPropertiesByStepIdAsync(stepId, CancellationToken.None).ConfigureAwait(false);

            properties.Count.ShouldBe(1);
            properties[0].PropertyName.ShouldBe("NewProp");
            properties[0].StepId.ShouldBe(stepId);
        }).ConfigureAwait(false);

        await Run<IDeploymentActionDataProvider>(async provider =>
        {
            var actions = await provider.GetDeploymentActionsByStepIdAsync(stepId, CancellationToken.None).ConfigureAwait(false);

            actions.Count.ShouldBe(1);
            actions[0].StepId.ShouldBe(stepId);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task UpdateStep_PersistsNameChange()
    {
        var processId = await SeedProcessAsync();
        var created = await CreateStepAsync(processId, "Before");

        await Run<IDeploymentStepService>(async service =>
        {
            var command = new UpdateDeploymentStepCommand
            {
                Id = created.Id,
                Step = new CreateOrUpdateDeploymentStepModel { Name = "After", StepType = "Action", Condition = "Success", StartTrigger = "", PackageRequirement = "" }
            };

            await service.UpdateDeploymentStepAsync(command, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IDeploymentStepDataProvider>(async provider =>
        {
            var step = await provider.GetDeploymentStepByIdAsync(created.Id, CancellationToken.None).ConfigureAwait(false);

            step.Name.ShouldBe("After");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task DeleteMiddleStep_ReindexesSurvivors()
    {
        var processId = await SeedProcessAsync();
        var step1 = await CreateStepAsync(processId, "Step A");
        var step2 = await CreateStepAsync(processId, "Step B");
        var step3 = await CreateStepAsync(processId, "Step C");

        await Run<IDeploymentStepService>(async service =>
        {
            await service.DeleteDeploymentStepsAsync(new DeleteDeploymentStepCommand { Ids = [step2.Id] }, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IDeploymentStepDataProvider>(async provider =>
        {
            var steps = await provider.GetDeploymentStepsByProcessIdAsync(processId, CancellationToken.None).ConfigureAwait(false);

            steps.Count.ShouldBe(2);
            steps[0].Name.ShouldBe("Step A");
            steps[0].StepOrder.ShouldBe(1);
            steps[1].Name.ShouldBe("Step C");
            steps[1].StepOrder.ShouldBe(2);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task DeleteFirstStep_ReindexesSurvivors()
    {
        var processId = await SeedProcessAsync();
        var step1 = await CreateStepAsync(processId, "Step A");
        var step2 = await CreateStepAsync(processId, "Step B");
        var step3 = await CreateStepAsync(processId, "Step C");

        await Run<IDeploymentStepService>(async service =>
        {
            await service.DeleteDeploymentStepsAsync(new DeleteDeploymentStepCommand { Ids = [step1.Id] }, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IDeploymentStepDataProvider>(async provider =>
        {
            var steps = await provider.GetDeploymentStepsByProcessIdAsync(processId, CancellationToken.None).ConfigureAwait(false);

            steps.Count.ShouldBe(2);
            steps[0].Name.ShouldBe("Step B");
            steps[0].StepOrder.ShouldBe(1);
            steps[1].Name.ShouldBe("Step C");
            steps[1].StepOrder.ShouldBe(2);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task DeleteMultipleSteps_ReindexesSurvivors()
    {
        var processId = await SeedProcessAsync();
        var step1 = await CreateStepAsync(processId, "Step A");
        var step2 = await CreateStepAsync(processId, "Step B");
        var step3 = await CreateStepAsync(processId, "Step C");
        var step4 = await CreateStepAsync(processId, "Step D");

        await Run<IDeploymentStepService>(async service =>
        {
            await service.DeleteDeploymentStepsAsync(new DeleteDeploymentStepCommand { Ids = [step1.Id, step3.Id] }, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IDeploymentStepDataProvider>(async provider =>
        {
            var steps = await provider.GetDeploymentStepsByProcessIdAsync(processId, CancellationToken.None).ConfigureAwait(false);

            steps.Count.ShouldBe(2);
            steps[0].Name.ShouldBe("Step B");
            steps[0].StepOrder.ShouldBe(1);
            steps[1].Name.ShouldBe("Step D");
            steps[1].StepOrder.ShouldBe(2);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task ReorderSteps_NormalizesToContiguous()
    {
        var processId = await SeedProcessAsync();
        var step1 = await CreateStepAsync(processId, "Step A");
        var step2 = await CreateStepAsync(processId, "Step B");
        var step3 = await CreateStepAsync(processId, "Step C");

        await Run<IDeploymentStepService>(async service =>
        {
            var command = new ReorderDeploymentStepsCommand
            {
                ProcessId = processId,
                StepOrders =
                [
                    new StepOrderItem { StepId = step1.Id, StepOrder = 10 },
                    new StepOrderItem { StepId = step2.Id, StepOrder = 20 },
                    new StepOrderItem { StepId = step3.Id, StepOrder = 30 }
                ]
            };

            await service.ReorderDeploymentStepsAsync(command, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IDeploymentStepDataProvider>(async provider =>
        {
            var steps = await provider.GetDeploymentStepsByProcessIdAsync(processId, CancellationToken.None).ConfigureAwait(false);

            steps.Count.ShouldBe(3);
            steps[0].Name.ShouldBe("Step A");
            steps[0].StepOrder.ShouldBe(1);
            steps[1].Name.ShouldBe("Step B");
            steps[1].StepOrder.ShouldBe(2);
            steps[2].Name.ShouldBe("Step C");
            steps[2].StepOrder.ShouldBe(3);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task ReorderSteps_ReversesOrder()
    {
        var processId = await SeedProcessAsync();
        var step1 = await CreateStepAsync(processId, "Step A");
        var step2 = await CreateStepAsync(processId, "Step B");
        var step3 = await CreateStepAsync(processId, "Step C");

        await Run<IDeploymentStepService>(async service =>
        {
            var command = new ReorderDeploymentStepsCommand
            {
                ProcessId = processId,
                StepOrders =
                [
                    new StepOrderItem { StepId = step3.Id, StepOrder = 1 },
                    new StepOrderItem { StepId = step2.Id, StepOrder = 2 },
                    new StepOrderItem { StepId = step1.Id, StepOrder = 3 }
                ]
            };

            await service.ReorderDeploymentStepsAsync(command, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IDeploymentStepDataProvider>(async provider =>
        {
            var steps = await provider.GetDeploymentStepsByProcessIdAsync(processId, CancellationToken.None).ConfigureAwait(false);

            steps.Count.ShouldBe(3);
            steps[0].Name.ShouldBe("Step C");
            steps[0].StepOrder.ShouldBe(1);
            steps[1].Name.ShouldBe("Step B");
            steps[1].StepOrder.ShouldBe(2);
            steps[2].Name.ShouldBe("Step A");
            steps[2].StepOrder.ShouldBe(3);
        }).ConfigureAwait(false);
    }

    private async Task<int> SeedProcessAsync()
    {
        var process = default(Squid.Core.Persistence.Entities.Deployments.DeploymentProcess);

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);
            process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        return process!.Id;
    }

    private async Task<DeploymentStepDto> CreateStepAsync(
        int processId,
        string name,
        List<StepPropertyModel> properties = null,
        List<CreateOrUpdateDeploymentActionModel> actions = null)
    {
        var result = await Run<IDeploymentStepService, DeploymentStepCreatedEvent>(async service =>
        {
            var command = new CreateDeploymentStepCommand
            {
                ProcessId = processId,
                Step = new CreateOrUpdateDeploymentStepModel
                {
                    Name = name,
                    StepType = "Action",
                    Condition = "Success",
                    StartTrigger = "",
                    PackageRequirement = "",
                    Properties = properties ?? [],
                    Actions = actions ?? []
                }
            };

            return await service.CreateDeploymentStepAsync(command, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return result.Data;
    }
}
