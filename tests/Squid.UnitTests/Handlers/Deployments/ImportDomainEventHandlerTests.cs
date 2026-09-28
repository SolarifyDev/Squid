using Squid.Core.Handlers.CommandHandlers.Deployments.Environment;
using Squid.Core.Handlers.CommandHandlers.Deployments.ExternalFeed;
using Squid.Core.Handlers.CommandHandlers.Deployments.LifeCycle;
using Squid.Core.Handlers.CommandHandlers.Deployments.Process;
using Squid.Core.Handlers.CommandHandlers.Deployments.Process.Step;
using Squid.Core.Handlers.CommandHandlers.Deployments.Project;
using Squid.Core.Handlers.CommandHandlers.Deployments.ProjectGroup;
using Squid.Core.Handlers.CommandHandlers.Deployments.Variable;
using Squid.Core.Services.Deployments.Environments;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Deployments.LifeCycle;
using Squid.Core.Services.Deployments.Process;
using Squid.Core.Services.Deployments.Process.Step;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.ProjectGroup;
using Squid.Core.Services.Deployments.Variables;
using Squid.Message.Commands.Deployments.Environment;
using Squid.Message.Commands.Deployments.ExternalFeed;
using Squid.Message.Commands.Deployments.LifeCycle;
using Squid.Message.Commands.Deployments.Process;
using Squid.Message.Commands.Deployments.Process.Step;
using Squid.Message.Commands.Deployments.Project;
using Squid.Message.Commands.Deployments.ProjectGroup;
using Squid.Message.Commands.Deployments.Variable;
using Squid.Message.Events.Deployments.Environment;
using Squid.Message.Events.Deployments.ExternalFeed;
using Squid.Message.Events.Deployments.LifeCycle;
using Squid.Message.Events.Deployments.Process;
using Squid.Message.Events.Deployments.Step;
using Squid.Message.Events.Deployments.Project;
using Squid.Message.Events.Deployments.ProjectGroup;
using Squid.Message.Models.Deployments.Environment;
using Squid.Message.Models.Deployments.ExternalFeed;
using Squid.Message.Models.Deployments.LifeCycle;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Project;
using Squid.Message.Models.Deployments.ProjectGroup;
using Squid.Message.Models.Deployments.Variable;
using Mediator.Net.Contracts;

namespace Squid.UnitTests.Handlers.Deployments;

public class ImportDomainEventHandlerTests
{
    [Fact]
    public async Task CreateProjectCommandHandler_PublishesReturnedEvent()
    {
        var service = new Mock<IProjectService>();
        var handler = new CreateProjectCommandHandler(service.Object);
        var command = new CreateProjectCommand
        {
            Project = new CreateOrUpdateProjectModel
            {
                Name = "App",
                SpaceId = 7,
                ProjectGroupId = 11,
                LifecycleId = 13
            }
        };
        var @event = new ProjectCreatedEvent
        {
            Data = new ProjectDto { Id = 1001, Name = "App", SpaceId = 7 }
        };
        var context = CreateContext(command);
        context.Setup(c => c.PublishAsync(@event, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        service.Setup(s => s.CreateProjectAsync(command, It.IsAny<CancellationToken>())).ReturnsAsync(@event);

        var result = await handler.Handle(context.Object, CancellationToken.None);

        result.Data.ShouldBe(@event.Data);
        context.Verify(c => c.PublishAsync(@event, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateEnvironmentCommandHandler_PublishesReturnedEvent()
    {
        var service = new Mock<IEnvironmentService>();
        var handler = new CreateEnvironmentCommandHandler(service.Object);
        var command = new CreateEnvironmentCommand { Name = "Production", SpaceId = 7 };
        var @event = new EnvironmentCreatedEvent
        {
            Data = new EnvironmentDto { Id = 12, Name = "Production", SpaceId = 7 }
        };
        var context = CreateContext(command);
        context.Setup(c => c.PublishAsync(@event, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        service.Setup(s => s.CreateEnvironmentAsync(command, It.IsAny<CancellationToken>())).ReturnsAsync(@event);

        var result = await handler.Handle(context.Object, CancellationToken.None);

        result.Data.Environment.ShouldBe(@event.Data);
        context.Verify(c => c.PublishAsync(@event, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateProjectGroupCommandHandler_PublishesReturnedEvent()
    {
        var service = new Mock<IProjectGroupService>();
        var handler = new CreateProjectGroupCommandHandler(service.Object);
        var command = new CreateProjectGroupCommand
        {
            ProjectGroup = new CreateOrUpdateProjectGroupModel
            {
                Name = "Platform",
                SpaceId = 7
            }
        };
        var @event = new ProjectGroupCreatedEvent
        {
            Data = new ProjectGroupDto { Id = 11, Name = "Platform", SpaceId = 7 }
        };
        var context = CreateContext(command);
        context.Setup(c => c.PublishAsync(@event, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        service.Setup(s => s.CreateProjectGroupAsync(command, It.IsAny<CancellationToken>())).ReturnsAsync(@event);

        var result = await handler.Handle(context.Object, CancellationToken.None);

        result.Data.ShouldBe(@event.Data);
        context.Verify(c => c.PublishAsync(@event, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateLifeCycleCommandHandler_PublishesReturnedEvent()
    {
        var service = new Mock<ILifeCycleService>();
        var handler = new CreateLifeCycleCommandHandler(service.Object);
        var command = new CreateLifeCycleCommand
        {
            LifecyclePhase = new CreateOrUpdateLifeCycleModel
            {
                Lifecycle = new LifeCycleModel { Name = "Standard", SpaceId = 7 },
                Phases = [new LifecyclePhaseModel { Name = "Phase 1", SortOrder = 1 }]
            }
        };
        var @event = new LifeCycleCreateEvent
        {
            Data = new CreateLifeCycleResponseData
            {
                LifecyclePhase = new LifecycleDetailDto
                {
                    Lifecycle = new LifeCycleDto { Id = 13, Name = "Standard", SpaceId = 7 },
                    Phases = [new LifecyclePhaseDto { Id = 14, Name = "Phase 1", SortOrder = 1 }]
                }
            }
        };
        var context = CreateContext(command);
        context.Setup(c => c.PublishAsync(@event, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        service.Setup(s => s.CreateLifeCycleAsync(command, It.IsAny<CancellationToken>())).ReturnsAsync(@event);

        var result = await handler.Handle(context.Object, CancellationToken.None);

        result.Data.ShouldBe(@event.Data);
        context.Verify(c => c.PublishAsync(@event, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateExternalFeedCommandHandler_PublishesReturnedEvent()
    {
        var service = new Mock<IExternalFeedService>();
        var handler = new CreateExternalFeedCommandHandler(service.Object);
        var command = new CreateExternalFeedCommand
        {
            Name = "Docker",
            SpaceId = 7
        };
        var @event = new ExternalFeedCreatedEvent
        {
            Data = new ExternalFeedDto { Id = 15, Name = "Docker", SpaceId = 7 }
        };
        var context = CreateContext(command);
        context.Setup(c => c.PublishAsync(@event, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        service.Setup(s => s.CreateExternalFeedAsync(command, It.IsAny<CancellationToken>())).ReturnsAsync(@event);

        var result = await handler.Handle(context.Object, CancellationToken.None);

        result.Data.ExternalFeed.ShouldBe(@event.Data);
        context.Verify(c => c.PublishAsync(@event, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateDeploymentProcessCommandHandler_PublishesReturnedEvent()
    {
        var service = new Mock<IDeploymentProcessService>();
        var handler = new CreateDeploymentProcessCommandHandler(service.Object);
        var command = new CreateDeploymentProcessCommand
        {
            ProjectId = 1001,
            Name = "Deployment Process",
            SpaceId = 7
        };
        var @event = new DeploymentProcessCreatedEvent
        {
            DeploymentProcess = new DeploymentProcessDto { Id = 1002, SpaceId = 7 }
        };
        var context = CreateContext(command);
        context.Setup(c => c.PublishAsync(@event, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        service.Setup(s => s.CreateDeploymentProcessAsync(command, It.IsAny<CancellationToken>())).ReturnsAsync(@event);

        var result = await handler.Handle(context.Object, CancellationToken.None);

        result.Data.DeploymentProcess.ShouldBe(@event.DeploymentProcess);
        context.Verify(c => c.PublishAsync(@event, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateDeploymentStepCommandHandler_PublishesReturnedEvent()
    {
        var service = new Mock<IDeploymentStepService>();
        var handler = new CreateDeploymentStepCommandHandler(service.Object);
        var command = new CreateDeploymentStepCommand
        {
            ProcessId = 1002,
            SpaceId = 7,
            Step = new CreateOrUpdateDeploymentStepModel
            {
                Name = "Deploy",
                Actions = [new CreateOrUpdateDeploymentActionModel { Name = "Run", ActionType = "Squid.Script" }]
            }
        };
        var @event = new DeploymentStepCreatedEvent
        {
            Data = new DeploymentStepDto { Id = 1003, ProcessId = 1002, Name = "Deploy" }
        };
        var context = CreateContext(command);
        context.Setup(c => c.PublishAsync(@event, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        service.Setup(s => s.CreateDeploymentStepAsync(command, It.IsAny<CancellationToken>())).ReturnsAsync(@event);

        var result = await handler.Handle(context.Object, CancellationToken.None);

        result.Data.ShouldBe(@event.Data);
        context.Verify(c => c.PublishAsync(@event, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateVariableSetCommandHandler_PreservesExistingSaveTimeEventPathWithoutPublishing()
    {
        var service = new Mock<IVariableService>();
        var handler = new UpdateVariableSetCommandHandler(service.Object);
        var command = new UpdateVariableSetCommand
        {
            Id = 1002,
            Name = "Variables",
            SpaceId = 7
        };
        var variableSet = new VariableSetDto { Id = 1002, Name = "Variables", SpaceId = 7 };
        var context = new Mock<IReceiveContext<UpdateVariableSetCommand>>(MockBehavior.Strict);
        context.SetupGet(c => c.Message).Returns(command);
        service.Setup(s => s.UpdateVariableSetAsync(command, It.IsAny<CancellationToken>())).ReturnsAsync(variableSet);

        var result = await handler.Handle(context.Object, CancellationToken.None);

        result.Data.VariableSet.ShouldBe(variableSet);
    }

    [Fact]
    public async Task CreateVariableSetCommandHandler_PreservesExistingSaveTimeEventPathWithoutPublishing()
    {
        var service = new Mock<IVariableService>();
        var handler = new CreateVariableSetCommandHandler(service.Object);
        var command = new CreateVariableSetCommand
        {
            Name = "Variables",
            SpaceId = 7
        };
        var variableSet = new VariableSetDto { Id = 1002, Name = "Variables", SpaceId = 7 };
        var context = new Mock<IReceiveContext<CreateVariableSetCommand>>(MockBehavior.Strict);
        context.SetupGet(c => c.Message).Returns(command);
        service.Setup(s => s.CreateVariableSetAsync(command, It.IsAny<CancellationToken>())).ReturnsAsync(variableSet);

        var result = await handler.Handle(context.Object, CancellationToken.None);

        result.Data.VariableSet.ShouldBe(variableSet);
    }

    private static Mock<IReceiveContext<TCommand>> CreateContext<TCommand>(TCommand command)
        where TCommand : class, IMessage
    {
        var context = new Mock<IReceiveContext<TCommand>>(MockBehavior.Strict);
        context.SetupGet(c => c.Message).Returns(command);
        return context;
    }
}
