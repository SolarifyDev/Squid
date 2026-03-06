using FluentValidation;
using Squid.Core.Middlewares.FluentMessageValidator;
using Squid.Message.Requests.Deployments.Deployment;

namespace Squid.Core.Validators.Deployments.Deployment;

public class ValidateDeploymentEnvironmentRequestValidator : FluentMessageValidator<ValidateDeploymentEnvironmentRequest>
{
    public ValidateDeploymentEnvironmentRequestValidator()
    {
        RuleFor(c => c.ReleaseId).GreaterThan(0);
        RuleFor(c => c.EnvironmentId).GreaterThan(0);
    }
}
