using Mediator.Net;
using Microsoft.AspNetCore.Mvc;
using Squid.Api.Controllers;
using Squid.Message.Requests.Deployments.Process;

namespace Squid.UnitTests.Controllers;

public class DeploymentProcessControllerTests
{
    [Fact]
    public async Task GetDeploymentProcessAsync_ForwardsExplicitRunbookContext()
    {
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.RequestAsync<GetDeploymentProcessRequest, GetDeploymentProcessResponse>(
                It.Is<GetDeploymentProcessRequest>(request =>
                    request.Id == 31
                    && request.ProjectId == 11
                    && request.SpaceId == 7
                    && request.RunbookId == 21),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetDeploymentProcessResponse
            {
                Data = new GetDeploymentProcessResponseData()
            });

        var controller = new DeploymentProcessController(mediator.Object);
        var result = await controller.GetDeploymentProcessAsync(
            id: 31,
            projectId: 11,
            spaceId: 7,
            runbookId: 21);

        result.ShouldBeOfType<OkObjectResult>();
    }
}
