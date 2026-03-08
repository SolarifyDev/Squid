using Squid.Core.Handlers.RequestHandlers.Deployments.Deployment;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Message.Models.Deployments.Deployment;
using Squid.Message.Models.Deployments.ServerTask;
using Squid.Message.Requests.Deployments.Deployment;

namespace Squid.UnitTests.Handlers.Deployments;

public class GetDeploymentRequestHandlerTests
{
    private readonly Mock<IDeploymentService> _deploymentService = new();
    private readonly GetDeploymentRequestHandler _handler;

    public GetDeploymentRequestHandlerTests()
    {
        _handler = new GetDeploymentRequestHandler(_deploymentService.Object);
    }

    [Fact]
    public async Task Handle_ReturnsDeploymentAndTaskDetails()
    {
        _deploymentService
            .Setup(service => service.GetDeploymentByIdAsync(
                It.IsAny<GetDeploymentRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetDeploymentResponse
            {
                Data = new GetDeploymentResponseData
                {
                    Deployment = new DeploymentDto { Id = 42, TaskId = 99, Name = "Deploy 1.0.0" },
                    TaskDetails = new ServerTaskDetailsDto
                    {
                        Task = new ServerTaskSummaryDto
                        {
                            Id = 99,
                            State = "Success"
                        }
                    }
                }
            });

        var response = await _handler.Handle(CreateContext(new GetDeploymentRequest { Id = 42 }).Object, CancellationToken.None);

        response.Data.ShouldNotBeNull();
        response.Data.Deployment.ShouldNotBeNull();
        response.Data.Deployment.Id.ShouldBe(42);
        response.Data.TaskDetails.ShouldNotBeNull();
        response.Data.TaskDetails.Task.ShouldNotBeNull();
        response.Data.TaskDetails.Task.Id.ShouldBe(99);
    }

    [Fact]
    public async Task Handle_PassesRequestToService()
    {
        GetDeploymentRequest capturedRequest = null;

        _deploymentService
            .Setup(service => service.GetDeploymentByIdAsync(
                It.IsAny<GetDeploymentRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<GetDeploymentRequest, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new GetDeploymentResponse { Data = new GetDeploymentResponseData() });

        await _handler.Handle(CreateContext(new GetDeploymentRequest
        {
            Id = 7,
            Verbose = true,
            Tail = 120
        }).Object, CancellationToken.None);

        capturedRequest.ShouldNotBeNull();
        capturedRequest.Id.ShouldBe(7);
        capturedRequest.Verbose.ShouldBe(true);
        capturedRequest.Tail.ShouldBe(120);
    }

    private static Mock<IReceiveContext<GetDeploymentRequest>> CreateContext(GetDeploymentRequest request)
    {
        var context = new Mock<IReceiveContext<GetDeploymentRequest>>();
        context.Setup(value => value.Message).Returns(request);
        return context;
    }
}
