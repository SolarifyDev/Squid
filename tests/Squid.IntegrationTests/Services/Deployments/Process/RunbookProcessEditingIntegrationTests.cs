using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Process;
using Squid.Core.Services.Deployments.Process.Step;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.Deployments.Process.Step;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Requests.Deployments.Process.Step;

namespace Squid.IntegrationTests.Services.Deployments.Process;

public class RunbookProcessEditingIntegrationTests : TestBase
{
    private static readonly Guid TestTopic = Guid.NewGuid();

    public RunbookProcessEditingIntegrationTests()
        : base(
            $"runbook_process_editing_{TestTopic:N}",
            $"squid_it_runbook_process_editing_{TestTopic:N}")
    {
    }

    [Fact]
    public async Task DeleteSteps_WithCrossProcessIds_RejectsWholeRequestWithoutSideEffects()
    {
        var fixture = await SeedFixtureAsync();

        await Run<IDeploymentStepService>(async service =>
        {
            await Should.ThrowAsync<ProcessContextMismatchException>(() =>
                service.DeleteDeploymentStepsAsync(
                    new DeleteDeploymentStepCommand
                    {
                        ProjectId = fixture.ProjectId,
                        RunbookId = fixture.RunbookId,
                        SpaceId = fixture.SpaceId,
                        Ids = [fixture.RunbookSteps[0].Id, fixture.ProjectSteps[0].Id]
                    },
                    CancellationToken.None));
        });

        await AssertStepsUnchangedAsync(fixture);
    }

    [Fact]
    public async Task ReorderSteps_WithCrossProcessId_RejectsWholeRequestWithoutSideEffects()
    {
        var fixture = await SeedFixtureAsync();

        await Run<IDeploymentStepService>(async service =>
        {
            await Should.ThrowAsync<ProcessContextMismatchException>(() =>
                service.ReorderDeploymentStepsAsync(
                    new ReorderDeploymentStepsCommand
                    {
                        ProcessId = fixture.RunbookProcessId,
                        ProjectId = fixture.ProjectId,
                        RunbookId = fixture.RunbookId,
                        SpaceId = fixture.SpaceId,
                        StepOrders =
                        [
                            new StepOrderItem { StepId = fixture.RunbookSteps[1].Id, StepOrder = 1 },
                            new StepOrderItem { StepId = fixture.RunbookSteps[0].Id, StepOrder = 2 },
                            new StepOrderItem { StepId = fixture.ProjectSteps[0].Id, StepOrder = 3 }
                        ]
                    },
                    CancellationToken.None));
        });

        await AssertStepsUnchangedAsync(fixture);
    }

    [Fact]
    public async Task UpdateStep_WithActionFromAnotherProcess_RejectsBeforeChangingRelatedData()
    {
        var fixture = await SeedFixtureAsync();
        var targetStep = fixture.RunbookSteps[0];
        var targetAction = fixture.RunbookActions[0];

        await Run<IDeploymentStepService>(async service =>
        {
            await Should.ThrowAsync<ProcessContextMismatchException>(() =>
                service.UpdateDeploymentStepAsync(
                    new UpdateDeploymentStepCommand
                    {
                        Id = targetStep.Id,
                        ProjectId = fixture.ProjectId,
                        RunbookId = fixture.RunbookId,
                        SpaceId = fixture.SpaceId,
                        Step = new CreateOrUpdateDeploymentStepModel
                        {
                            Name = "Mutated name",
                            StepType = "Action",
                            Condition = "Failure",
                            Properties =
                            [
                                new StepPropertyModel { PropertyName = "Squid.Step.TargetRoles", PropertyValue = "mutated" }
                            ],
                            Actions =
                            [
                                new CreateOrUpdateDeploymentActionModel
                                {
                                    Id = fixture.ProjectActions[0].Id,
                                    Name = "Mutated action",
                                    ActionType = "Squid.Script",
                                    Properties =
                                    [
                                        new ActionPropertyModel { PropertyName = "Squid.Action.TargetRoles", PropertyValue = "mutated" }
                                    ],
                                    Environments = [fixture.OtherEnvironmentId],
                                    ExcludedEnvironments = [fixture.OtherEnvironmentId],
                                    Channels = [fixture.OtherChannelId]
                                }
                            ]
                        }
                    },
                    CancellationToken.None));
        });

        await AssertStepsUnchangedAsync(fixture);
        await Run<SquidDbContext>(async db =>
        {
            db.ChangeTracker.Clear();

            var stepProperties = await db.Set<DeploymentStepProperty>()
                .Where(property => property.StepId == targetStep.Id)
                .ToListAsync();
            stepProperties.ShouldHaveSingleItem().PropertyValue.ShouldBe("original-step");

            var actionProperties = await db.Set<DeploymentActionProperty>()
                .Where(property => property.ActionId == targetAction.Id)
                .ToListAsync();
            actionProperties.ShouldHaveSingleItem().PropertyValue.ShouldBe("original-action");

            var environments = await db.Set<ActionEnvironment>()
                .Where(environment => environment.ActionId == targetAction.Id)
                .Select(environment => environment.EnvironmentId)
                .ToListAsync();
            environments.ShouldBe([fixture.EnvironmentId]);

            var excludedEnvironments = await db.Set<ActionExcludedEnvironment>()
                .Where(environment => environment.ActionId == targetAction.Id)
                .Select(environment => environment.EnvironmentId)
                .ToListAsync();
            excludedEnvironments.ShouldBe([fixture.ExcludedEnvironmentId]);

            var channels = await db.Set<ActionChannel>()
                .Where(channel => channel.ActionId == targetAction.Id)
                .Select(channel => channel.ChannelId)
                .ToListAsync();
            channels.ShouldBe([fixture.ChannelId]);
        });
    }

    [Theory]
    [InlineData("runbook")]
    [InlineData("project")]
    [InlineData("space")]
    public async Task StepMutation_WithMismatchedContext_IsRejectedWithoutSideEffects(string mismatch)
    {
        var fixture = await SeedFixtureAsync();

        await Run<IDeploymentStepService>(async service =>
        {
            var command = new UpdateDeploymentStepCommand
            {
                Id = fixture.RunbookSteps[0].Id,
                ProjectId = fixture.ProjectId,
                RunbookId = fixture.RunbookId,
                SpaceId = fixture.SpaceId,
                Step = BuildReplacementStep("rejected")
            };

            if (mismatch == "runbook")
            {
                command.RunbookId = fixture.SecondRunbookId;
            }
            else if (mismatch == "project")
            {
                command.ProjectId = fixture.SecondProjectId;
            }
            else
            {
                command.SpaceId = fixture.OtherSpaceId;
            }

            await Should.ThrowAsync<ProcessContextMismatchException>(() =>
                service.UpdateDeploymentStepAsync(command, CancellationToken.None));
        });

        await AssertStepsUnchangedAsync(fixture);
    }

    [Fact]
    public async Task RunbookProcess_CannotBeLoadedAsProjectDeploymentProcess()
    {
        var fixture = await SeedFixtureAsync();

        await Run<IDeploymentProcessService>(async service =>
        {
            await Should.ThrowAsync<ProcessContextMismatchException>(() =>
                service.GetProjectDeploymentProcessAsync(
                    new Squid.Message.Requests.Deployments.Process.GetDeploymentProcessRequest
                    {
                        Id = fixture.RunbookProcessId,
                        ProjectId = fixture.ProjectId,
                        RunbookId = fixture.RunbookId,
                        SpaceId = fixture.SpaceId
                    },
                    CancellationToken.None));
        });
    }

    [Fact]
    public async Task UpdateAndReorderRunbookSteps_EditsOnlyRunbookDraft()
    {
        var fixture = await SeedFixtureAsync();
        var firstStep = fixture.RunbookSteps[0];
        var firstAction = fixture.RunbookActions[0];

        await Run<IDeploymentStepService>(async service =>
        {
            var updated = await service.UpdateDeploymentStepAsync(
                new UpdateDeploymentStepCommand
                {
                    Id = firstStep.Id,
                    ProjectId = fixture.ProjectId,
                    RunbookId = fixture.RunbookId,
                    SpaceId = fixture.SpaceId,
                    Step = new CreateOrUpdateDeploymentStepModel
                    {
                        Name = "Updated Runbook Step",
                        StepType = "Action",
                        Condition = "Failure",
                        StartTrigger = "After previous step",
                        PackageRequirement = "Required",
                        Properties =
                        [
                            new StepPropertyModel { PropertyName = "Squid.Step.TargetRoles", PropertyValue = "updated-step" }
                        ],
                        Actions =
                        [
                            new CreateOrUpdateDeploymentActionModel
                            {
                                Id = firstAction.Id,
                                Name = "Updated Runbook Action",
                                ActionType = "Squid.Script",
                                Properties =
                                [
                                    new ActionPropertyModel { PropertyName = "Squid.Action.TargetRoles", PropertyValue = "updated-action" }
                                ],
                                Environments = [fixture.EnvironmentId],
                                ExcludedEnvironments = [fixture.ExcludedEnvironmentId],
                                Channels = [fixture.ChannelId]
                            }
                        ]
                    }
                },
                CancellationToken.None);

            updated.Data.Name.ShouldBe("Updated Runbook Step");
            updated.Data.Properties.ShouldContain(property => property.PropertyValue == "updated-step");
            updated.Data.Actions.ShouldHaveSingleItem();
            updated.Data.Actions[0].Id.ShouldBe(firstAction.Id);
            updated.Data.Actions[0].Environments.ShouldBe([fixture.EnvironmentId]);
            updated.Data.Actions[0].ExcludedEnvironments.ShouldBe([fixture.ExcludedEnvironmentId]);
            updated.Data.Actions[0].Channels.ShouldBe([fixture.ChannelId]);

            await service.ReorderDeploymentStepsAsync(
                new ReorderDeploymentStepsCommand
                {
                    ProcessId = fixture.RunbookProcessId,
                    ProjectId = fixture.ProjectId,
                    RunbookId = fixture.RunbookId,
                    SpaceId = fixture.SpaceId,
                    StepOrders =
                    [
                        new StepOrderItem { StepId = fixture.RunbookSteps[1].Id, StepOrder = 1 },
                        new StepOrderItem { StepId = fixture.RunbookSteps[0].Id, StepOrder = 2 }
                    ]
                },
                CancellationToken.None);

            var steps = await service.GetDeploymentStepsAsync(
                new GetDeploymentStepsRequest
                {
                    ProcessId = fixture.RunbookProcessId,
                    ProjectId = fixture.ProjectId,
                    RunbookId = fixture.RunbookId,
                    SpaceId = fixture.SpaceId
                },
                CancellationToken.None);

            steps.Data.Steps.Select(step => step.Name).ShouldBe(
                ["Runbook Step 2", "Updated Runbook Step"]);
        });

        await Run<SquidDbContext>(async db =>
        {
            db.ChangeTracker.Clear();

            var projectSteps = await db.Set<DeploymentStep>()
                .Where(step => step.ProcessId == fixture.ProjectProcessId)
                .OrderBy(step => step.StepOrder)
                .ToListAsync();
            projectSteps.Select(step => (step.Name, step.Condition, step.StepOrder))
                .ShouldBe([("Project Step", "Success", 1)]);

            var secondRunbookSteps = await db.Set<DeploymentStep>()
                .Where(step => step.ProcessId == fixture.SecondRunbookProcessId)
                .ToListAsync();
            secondRunbookSteps.Select(step => (step.Name, step.Condition)).ShouldBe(
                [("Second Runbook Step", "Success")]);

            var projectAction = await db.Set<DeploymentAction>()
                .SingleAsync(action => action.Id == fixture.ProjectActions[0].Id);
            (projectAction.Name, projectAction.ActionType).ShouldBe(("Project Action", "Squid.Script"));

            var projectActionProperties = await db.Set<DeploymentActionProperty>()
                .Where(property => property.ActionId == projectAction.Id)
                .ToListAsync();
            projectActionProperties.ShouldHaveSingleItem().PropertyValue.ShouldBe("original-action");

            var secondRunbookAction = await db.Set<DeploymentAction>()
                .SingleAsync(action => action.Id == fixture.SecondRunbookActions[0].Id);
            (secondRunbookAction.Name, secondRunbookAction.ActionType).ShouldBe(
                ("Second Runbook Action", "Squid.Script"));
        });
    }

    private async Task<Fixture> SeedFixtureAsync()
    {
        Fixture seeded = null;
        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
            {
                var builder = new TestDataBuilder(repository, unitOfWork);
                var project = await builder.CreateProjectDeploymentProcessAsync("Runbook Editing Project")
                    .ConfigureAwait(false);

                var secondProject = await builder.CreateProjectDeploymentProcessAsync("Second Runbook Project")
                    .ConfigureAwait(false);
                var runbook = await builder.CreateRunbookAsync(project.ProjectId, project.SpaceId, "Primary Runbook")
                    .ConfigureAwait(false);
                var secondRunbook = await builder.CreateRunbookAsync(
                    secondProject.ProjectId,
                    secondProject.SpaceId,
                    "Second Runbook").ConfigureAwait(false);

                var projectStep = await builder.CreateDeploymentStepAsync(
                    project.Id,
                    1,
                    "Project Step").ConfigureAwait(false);
                var projectAction = await builder.CreateDeploymentActionAsync(
                    projectStep.Id,
                    1,
                    "Project Action").ConfigureAwait(false);
                await builder.CreateActionPropertiesAsync(
                    projectAction.Id,
                    ("Squid.Action.TargetRoles", "original-action")).ConfigureAwait(false);

                var environment = await builder.CreateEnvironmentAsync("Editing Environment").ConfigureAwait(false);
                var excludedEnvironment = await builder.CreateEnvironmentAsync("Excluded Environment").ConfigureAwait(false);
                var otherEnvironment = await builder.CreateEnvironmentAsync("Other Environment").ConfigureAwait(false);
                var channel = await builder.CreateChannelAsync(project.ProjectId).ConfigureAwait(false);
                var otherChannel = await builder.CreateChannelAsync(
                    secondProject.ProjectId,
                    name: "Other Channel").ConfigureAwait(false);

                var firstRunbookStep = await builder.CreateDeploymentStepAsync(
                    runbook.ProcessId,
                    1,
                    "Runbook Step 1").ConfigureAwait(false);
                var secondRunbookStep = await builder.CreateDeploymentStepAsync(
                    runbook.ProcessId,
                    2,
                    "Runbook Step 2").ConfigureAwait(false);
                var firstRunbookAction = await builder.CreateDeploymentActionAsync(
                    firstRunbookStep.Id,
                    1,
                    "Runbook Action").ConfigureAwait(false);
                await builder.CreateStepPropertiesAsync(
                    firstRunbookStep.Id,
                    ("Squid.Step.TargetRoles", "original-step")).ConfigureAwait(false);
                await builder.CreateActionPropertiesAsync(
                    firstRunbookAction.Id,
                    ("Squid.Action.TargetRoles", "original-action")).ConfigureAwait(false);
                await builder.CreateActionEnvironmentsAsync(
                    firstRunbookAction.Id,
                    environment.Id).ConfigureAwait(false);
                await builder.CreateActionExcludedEnvironmentsAsync(
                    firstRunbookAction.Id,
                    excludedEnvironment.Id).ConfigureAwait(false);
                await builder.CreateActionChannelsAsync(
                    firstRunbookAction.Id,
                    channel.Id).ConfigureAwait(false);

                var secondRunbookStepEntity = await builder.CreateDeploymentStepAsync(
                    secondRunbook.ProcessId,
                    1,
                    "Second Runbook Step").ConfigureAwait(false);
                var secondRunbookAction = await builder.CreateDeploymentActionAsync(
                    secondRunbookStepEntity.Id,
                    1,
                    "Second Runbook Action").ConfigureAwait(false);

                seeded = new Fixture
                {
                    ProjectId = project.ProjectId,
                    ProjectProcessId = project.Id,
                    RunbookId = runbook.Id,
                    RunbookProcessId = runbook.ProcessId,
                    SecondProjectId = secondProject.ProjectId,
                    SecondRunbookId = secondRunbook.Id,
                    SecondRunbookProcessId = secondRunbook.ProcessId,
                    SpaceId = project.SpaceId,
                    OtherSpaceId = project.SpaceId + 1,
                    ProjectSteps = [projectStep],
                    ProjectActions = [projectAction],
                    RunbookSteps = [firstRunbookStep, secondRunbookStep],
                    RunbookActions = [firstRunbookAction],
                    SecondRunbookActions = [secondRunbookAction],
                    EnvironmentId = environment.Id,
                    ExcludedEnvironmentId = excludedEnvironment.Id,
                    OtherEnvironmentId = otherEnvironment.Id,
                    ChannelId = channel.Id,
                    OtherChannelId = otherChannel.Id,
                    RunbookStepIds = [firstRunbookStep.Id, secondRunbookStep.Id],
                    ProjectStepIds = [projectStep.Id],
                    RunbookActionIds = [firstRunbookAction.Id],
                    ProjectActionIds = [projectAction.Id]
                };
            });

        return seeded;
    }

    private async Task AssertStepsUnchangedAsync(Fixture fixture)
    {
        await Run<SquidDbContext>(async db =>
        {
            db.ChangeTracker.Clear();

            var runbookSteps = await db.Set<DeploymentStep>()
                .Where(step => step.ProcessId == fixture.RunbookProcessId)
                .OrderBy(step => step.StepOrder)
                .ToListAsync();
            runbookSteps.Select(step => (step.Name, step.Condition, step.StepOrder)).ShouldBe(
                [
                    ("Runbook Step 1", "Success", 1),
                    ("Runbook Step 2", "Success", 2)
                ]);

            var projectStep = await db.Set<DeploymentStep>()
                .SingleAsync(step => step.Id == fixture.ProjectSteps[0].Id);
            (projectStep.Name, projectStep.StepOrder).ShouldBe(("Project Step", 1));

            var stepProperties = await db.Set<DeploymentStepProperty>()
                .Where(property => property.StepId == fixture.RunbookSteps[0].Id)
                .ToListAsync();
            stepProperties.ShouldHaveSingleItem().PropertyValue.ShouldBe("original-step");
        });
    }

    private static CreateOrUpdateDeploymentStepModel BuildReplacementStep(string marker)
    {
        return new CreateOrUpdateDeploymentStepModel
        {
            Name = $"Mutated {marker}",
            StepType = "Action",
            Condition = "Failure",
            Properties =
            [
                new StepPropertyModel { PropertyName = "Squid.Step.TargetRoles", PropertyValue = marker }
            ],
            Actions = []
        };
    }

    private sealed class Fixture
    {
        public int ProjectId { get; init; }
        public int ProjectProcessId { get; init; }
        public int RunbookId { get; init; }
        public int RunbookProcessId { get; init; }
        public int SecondProjectId { get; init; }
        public int SecondRunbookId { get; init; }
        public int SecondRunbookProcessId { get; init; }
        public int SpaceId { get; init; }
        public int OtherSpaceId { get; init; }
        public int EnvironmentId { get; init; }
        public int ExcludedEnvironmentId { get; init; }
        public int OtherEnvironmentId { get; init; }
        public int ChannelId { get; init; }
        public int OtherChannelId { get; init; }
        public List<DeploymentStep> ProjectSteps { get; init; }
        public List<DeploymentAction> ProjectActions { get; init; }
        public List<DeploymentStep> RunbookSteps { get; init; }
        public List<DeploymentAction> RunbookActions { get; init; }
        public List<DeploymentAction> SecondRunbookActions { get; init; }
        public List<int> RunbookStepIds { get; init; }
        public List<int> ProjectStepIds { get; init; }
        public List<int> RunbookActionIds { get; init; }
        public List<int> ProjectActionIds { get; init; }
    }
}
