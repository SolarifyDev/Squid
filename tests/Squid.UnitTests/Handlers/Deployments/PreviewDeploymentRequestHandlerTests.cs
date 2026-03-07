using Squid.Core.Handlers.RequestHandlers.Deployments.Deployment;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Message.Models.Deployments.Deployment;
using Squid.Message.Requests.Deployments.Deployment;

namespace Squid.UnitTests.Handlers.Deployments;

public class PreviewDeploymentRequestHandlerTests
{
    private readonly Mock<IDeploymentService> _deploymentService = new();
    private readonly PreviewDeploymentRequestHandler _handler;

    public PreviewDeploymentRequestHandlerTests()
    {
        _handler = new PreviewDeploymentRequestHandler(_deploymentService.Object);
    }

    [Fact]
    public async Task Handle_ReturnsPreviewResult()
    {
        _deploymentService
            .Setup(service => service.PreviewDeploymentAsync(
                It.IsAny<DeploymentRequestPayload>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentPreviewResult
            {
                CanDeploy = false,
                BlockingReasons = ["No target machines match the required roles for any runnable step."],
                AvailableMachineCount = 2,
                LifecycleId = 12,
                AllowedEnvironmentIds = [1, 2]
            });

        var response = await _handler.Handle(CreateContext(new PreviewDeploymentRequest
        {
            DeploymentRequestPayload = new DeploymentRequestPayload
            {
                ReleaseId = 1,
                EnvironmentId = 2
            }
        }).Object, CancellationToken.None);

        response.CanDeploy.ShouldBeFalse();
        response.BlockingReasons.ShouldContain("No target machines match the required roles for any runnable step.");
        response.AvailableMachineCount.ShouldBe(2);
        response.LifecycleId.ShouldBe(12);
        response.AllowedEnvironmentIds.ShouldBe([1, 2], ignoreOrder: true);
    }

    [Fact]
    public async Task Handle_PassesRequestPayloadToService()
    {
        DeploymentRequestPayload capturedPayload = null;

        _deploymentService
            .Setup(service => service.PreviewDeploymentAsync(
                It.IsAny<DeploymentRequestPayload>(),
                It.IsAny<CancellationToken>()))
            .Callback<DeploymentRequestPayload, CancellationToken>((payload, _) => capturedPayload = payload)
            .ReturnsAsync(new DeploymentPreviewResult { CanDeploy = true });

        await _handler.Handle(CreateContext(new PreviewDeploymentRequest
        {
            DeploymentRequestPayload = new DeploymentRequestPayload
            {
                ReleaseId = 3,
                EnvironmentId = 5,
                SpecificMachineIds = [1, 2],
                ExcludedMachineIds = [4],
                SkipActionIds = [8]
            }
        }).Object, CancellationToken.None);

        capturedPayload.ShouldNotBeNull();
        capturedPayload.ReleaseId.ShouldBe(3);
        capturedPayload.EnvironmentId.ShouldBe(5);
        capturedPayload.SpecificMachineIds.ShouldBe([1, 2], ignoreOrder: true);
        capturedPayload.ExcludedMachineIds.ShouldBe([4], ignoreOrder: true);
        capturedPayload.SkipActionIds.ShouldBe([8], ignoreOrder: true);
    }

    private static Mock<IReceiveContext<PreviewDeploymentRequest>> CreateContext(PreviewDeploymentRequest request)
    {
        var context = new Mock<IReceiveContext<PreviewDeploymentRequest>>();
        context.Setup(value => value.Message).Returns(request);
        return context;
    }
}
