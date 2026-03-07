using System.Collections.Generic;
using Squid.Core.Handlers.RequestHandlers.Deployments.Deployment;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Validation;
using Squid.Message.Models.Deployments.Deployment;
using Squid.Message.Requests.Deployments.Deployment;

namespace Squid.UnitTests.Handlers.Deployments;

public class ValidateDeploymentEnvironmentRequestHandlerTests
{
    private readonly Mock<IDeploymentService> _deploymentService = new();
    private readonly Mock<IDeploymentValidationOrchestrator> _deploymentValidationOrchestrator = new();
    private readonly ValidateDeploymentEnvironmentRequestHandler _handler;

    public ValidateDeploymentEnvironmentRequestHandlerTests()
    {
        _handler = new ValidateDeploymentEnvironmentRequestHandler(
            _deploymentService.Object,
            _deploymentValidationOrchestrator.Object);
    }

    [Fact]
    public async Task Handle_MergesEnvironmentValidationAndRuleIssues()
    {
        _deploymentService
            .Setup(x => x.ValidateDeploymentEnvironmentAsync(
                It.IsAny<DeploymentValidationContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentEnvironmentValidationResult
            {
                IsValid = false,
                Reasons = ["No available machines"],
                AvailableMachineCount = 0
            });

        var report = new DeploymentValidationReport();
        report.AddBlockingIssue(DeploymentValidationIssueCode.ProjectDisabled, "Project is disabled.");

        _deploymentValidationOrchestrator
            .Setup(x => x.ValidateAsync(
                DeploymentValidationStage.Precheck,
                It.IsAny<DeploymentValidationContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(report);

        var response = await _handler.Handle(CreateContext(new ValidateDeploymentEnvironmentRequest
        {
            ReleaseId = 1,
            EnvironmentId = 2
        }).Object, CancellationToken.None);

        response.IsValid.ShouldBeFalse();
        response.Reasons.ShouldContain("No available machines");
        response.Reasons.ShouldContain("Project is disabled.");
    }

    [Fact]
    public async Task Handle_NormalizesMachineAndSkipActionIds()
    {
        DeploymentValidationContext capturedValidatorContext = null;
        DeploymentValidationContext capturedContext = null;

        _deploymentService
            .Setup(x => x.ValidateDeploymentEnvironmentAsync(
                It.IsAny<DeploymentValidationContext>(),
                It.IsAny<CancellationToken>()))
            .Callback<DeploymentValidationContext, CancellationToken>((context, _) => capturedValidatorContext = context)
            .ReturnsAsync(new DeploymentEnvironmentValidationResult { IsValid = true });

        _deploymentValidationOrchestrator
            .Setup(x => x.ValidateAsync(
                DeploymentValidationStage.Precheck,
                It.IsAny<DeploymentValidationContext>(),
                It.IsAny<CancellationToken>()))
            .Callback<DeploymentValidationStage, DeploymentValidationContext, CancellationToken>((_, context, _) => capturedContext = context)
            .ReturnsAsync(new DeploymentValidationReport());

        await _handler.Handle(CreateContext(new ValidateDeploymentEnvironmentRequest
        {
            ReleaseId = 1,
            EnvironmentId = 2,
            SpecificMachineIds = ["1", " 2 ", "x", "0"],
            ExcludedMachineIds = ["2", "5", "", "-1"],
            SkipActionIds = [0, 3, -1]
        }).Object, CancellationToken.None);

        capturedValidatorContext.ShouldNotBeNull();
        capturedContext.ShouldNotBeNull();

        capturedValidatorContext.SpecificMachineIds.SetEquals([1, 2]).ShouldBeTrue();
        capturedValidatorContext.ExcludedMachineIds.SetEquals([2, 5]).ShouldBeTrue();
        capturedContext.SkipActionIds.SetEquals([3]).ShouldBeTrue();
    }

    private static Mock<IReceiveContext<ValidateDeploymentEnvironmentRequest>> CreateContext(ValidateDeploymentEnvironmentRequest request)
    {
        var context = new Mock<IReceiveContext<ValidateDeploymentEnvironmentRequest>>();
        context.Setup(x => x.Message).Returns(request);
        return context;
    }
}
