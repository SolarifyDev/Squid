using System;
using Squid.Core.Validators.Deployments.Deployment;
using Squid.Message.Models.Deployments.Deployment;
using Squid.Message.Requests.Deployments.Deployment;

namespace Squid.UnitTests.Validators.Deployments.DeploymentCommands;

public class PreviewDeploymentRequestValidatorTests
{
    private readonly PreviewDeploymentRequestValidator _validator = new();

    [Fact]
    public void Valid_Request_Passes()
    {
        var request = new PreviewDeploymentRequest
        {
            DeploymentRequestPayload = new DeploymentRequestPayload
            {
                ReleaseId = 1,
                EnvironmentId = 2
            }
        };

        var result = _validator.Validate(request);

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_ReleaseId_Fails(int releaseId)
    {
        var request = new PreviewDeploymentRequest
        {
            DeploymentRequestPayload = new DeploymentRequestPayload
            {
                ReleaseId = releaseId,
                EnvironmentId = 1
            }
        };

        var result = _validator.Validate(request);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName.Contains("ReleaseId"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_EnvironmentId_Fails(int environmentId)
    {
        var request = new PreviewDeploymentRequest
        {
            DeploymentRequestPayload = new DeploymentRequestPayload
            {
                ReleaseId = 1,
                EnvironmentId = environmentId
            }
        };

        var result = _validator.Validate(request);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName.Contains("EnvironmentId"));
    }

    [Fact]
    public void SkipActionIds_WithInvalidValue_Fails()
    {
        var request = new PreviewDeploymentRequest
        {
            DeploymentRequestPayload = new DeploymentRequestPayload
            {
                ReleaseId = 1,
                EnvironmentId = 1,
                SkipActionIds = [0, -1]
            }
        };

        var result = _validator.Validate(request);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName.Contains("SkipActionIds"));
    }

    [Fact]
    public void SpecificMachineIds_WithInvalidValue_Fails()
    {
        var request = new PreviewDeploymentRequest
        {
            DeploymentRequestPayload = new DeploymentRequestPayload
            {
                ReleaseId = 1,
                EnvironmentId = 1,
                SpecificMachineIds = [1, 0]
            }
        };

        var result = _validator.Validate(request);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName.Contains("SpecificMachineIds"));
    }

    [Fact]
    public void Overlapping_SpecificAndExcludedMachineIds_Fails()
    {
        var request = new PreviewDeploymentRequest
        {
            DeploymentRequestPayload = new DeploymentRequestPayload
            {
                ReleaseId = 1,
                EnvironmentId = 1,
                SpecificMachineIds = [1, 2],
                ExcludedMachineIds = [2]
            }
        };

        var result = _validator.Validate(request);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.ErrorMessage.Contains("cannot overlap"));
    }

    [Fact]
    public void QueueTimeExpiry_WithoutQueueTime_Fails()
    {
        var request = new PreviewDeploymentRequest
        {
            DeploymentRequestPayload = new DeploymentRequestPayload
            {
                ReleaseId = 1,
                EnvironmentId = 1,
                QueueTimeExpiry = DateTimeOffset.UtcNow.AddMinutes(30)
            }
        };

        var result = _validator.Validate(request);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.ErrorMessage.Contains("QueueTimeExpiry"));
    }
}
