using Squid.Core.Validators.Deployments.Deployment;
using Squid.Message.Requests.Deployments.Deployment;

namespace Squid.UnitTests.Validators.Deployments.DeploymentCommands;

public class ValidateDeploymentEnvironmentRequestValidatorTests
{
    private readonly ValidateDeploymentEnvironmentRequestValidator _validator = new();

    [Fact]
    public void Valid_Request_Passes()
    {
        var request = new ValidateDeploymentEnvironmentRequest
        {
            ReleaseId = 1,
            EnvironmentId = 2
        };

        var result = _validator.Validate(request);

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_ReleaseId_Fails(int releaseId)
    {
        var request = new ValidateDeploymentEnvironmentRequest
        {
            ReleaseId = releaseId,
            EnvironmentId = 1
        };

        var result = _validator.Validate(request);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(x => x.PropertyName == "ReleaseId");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_EnvironmentId_Fails(int environmentId)
    {
        var request = new ValidateDeploymentEnvironmentRequest
        {
            ReleaseId = 1,
            EnvironmentId = environmentId
        };

        var result = _validator.Validate(request);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(x => x.PropertyName == "EnvironmentId");
    }
}
