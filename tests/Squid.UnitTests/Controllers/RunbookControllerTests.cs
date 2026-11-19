using Mediator.Net;
using Microsoft.AspNetCore.Mvc;
using Squid.Api.Controllers;
using Squid.Message.Commands.Deployments.Runbook;
using Squid.Message.Models.Deployments.Runbook;
using Squid.Message.Requests.Deployments.Runbook;

namespace Squid.UnitTests.Controllers;

public class RunbookControllerTests
{
    [Fact]
    public async Task CreateAsync_DispatchesCreateCommandAndReturnsEnvelope()
    {
        var mediator = new Mock<IMediator>();
        var command = CreateCommand(projectId: 11, spaceId: 7);
        var response = new CreateRunbookResponse
        {
            Data = new RunbookDto { Id = 21, ProjectId = 11, SpaceId = 7, ProcessId = 31 }
        };

        mediator
            .Setup(m => m.SendAsync<CreateRunbookCommand, CreateRunbookResponse>(
                command,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var controller = new RunbookController(mediator.Object);
        var result = await controller.CreateAsync(command, CancellationToken.None);

        var ok = result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBeSameAs(response);
    }

    [Fact]
    public async Task ListAsync_DispatchesProjectScopedListRequest()
    {
        var mediator = new Mock<IMediator>();
        var request = new GetRunbooksRequest
        {
            ProjectId = 11,
            SpaceId = 7,
            PageIndex = 2,
            PageSize = 5
        };
        var response = new GetRunbooksResponse
        {
            Data = new GetRunbooksResponseData
            {
                Count = 6,
                Runbooks = [new RunbookDto { Id = 21, ProjectId = 11, SpaceId = 7 }]
            }
        };

        mediator
            .Setup(m => m.RequestAsync<GetRunbooksRequest, GetRunbooksResponse>(
                request,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var controller = new RunbookController(mediator.Object);
        var result = await controller.ListAsync(request, CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeSameAs(response);
    }

    [Fact]
    public async Task DetailAsync_RequiresProjectAndForwardsOptionalSpaceContext()
    {
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.RequestAsync<GetRunbookRequest, GetRunbookResponse>(
                It.Is<GetRunbookRequest>(request =>
                    request.Id == 21
                    && request.ProjectId == 11
                    && request.SpaceId == 7),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetRunbookResponse
            {
                Data = new RunbookDto { Id = 21, ProjectId = 11, SpaceId = 7, ProcessId = 31 }
            });

        var controller = new RunbookController(mediator.Object);
        var result = await controller.DetailAsync(
            id: 21,
            projectId: 11,
            spaceId: 7,
            CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task DetailAsync_WhenProjectIsMissing_DispatchesForStructuredValidation()
    {
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.RequestAsync<GetRunbookRequest, GetRunbookResponse>(
                It.Is<GetRunbookRequest>(request =>
                    request.Id == 21
                    && request.ProjectId == 0
                    && request.SpaceId == 7),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetRunbookResponse());
        var controller = new RunbookController(mediator.Object);

        var result = await controller.DetailAsync(
            id: 21,
            projectId: null,
            spaceId: 7,
            CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        mediator.Verify(m => m.RequestAsync<GetRunbookRequest, GetRunbookResponse>(
            It.IsAny<GetRunbookRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateCopyAndDelete_ReturnEnvelopes()
    {
        var mediator = new Mock<IMediator>();
        var update = new UpdateRunbookCommand
        {
            Id = 21,
            ProjectId = 11,
            SpaceId = 7,
            Runbook = new UpdateRunbookModel { Name = "Updated" }
        };
        var copy = new CopyRunbookCommand
        {
            SourceRunbookId = 21,
            ProjectId = 11,
            SpaceId = 7
        };
        var delete = new DeleteRunbookCommand
        {
            Id = 21,
            ProjectId = 11,
            SpaceId = 7
        };

        mediator
            .Setup(m => m.SendAsync<UpdateRunbookCommand, UpdateRunbookResponse>(
                update,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateRunbookResponse { Data = new RunbookDto { Id = 21 } });
        mediator
            .Setup(m => m.SendAsync<CopyRunbookCommand, CopyRunbookResponse>(
                copy,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CopyRunbookResponse { Data = new RunbookDto { Id = 22 } });
        mediator
            .Setup(m => m.SendAsync<DeleteRunbookCommand, DeleteRunbookResponse>(
                delete,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteRunbookResponse
            {
                Data = new DeleteRunbookResponseData { Id = 21 }
            });

        var controller = new RunbookController(mediator.Object);

        (await controller.UpdateAsync(update, CancellationToken.None))
            .ShouldBeOfType<OkObjectResult>();
        (await controller.CopyAsync(copy, CancellationToken.None))
            .ShouldBeOfType<OkObjectResult>();
        (await controller.DeleteAsync(delete, CancellationToken.None))
            .ShouldBeOfType<OkObjectResult>();
    }

    private static CreateRunbookCommand CreateCommand(int projectId, int spaceId) => new()
    {
        Runbook = new CreateRunbookModel
        {
            ProjectId = projectId,
            SpaceId = spaceId,
            Name = "Payment Recovery"
        }
    };
}
