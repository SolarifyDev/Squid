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

        RuleForEach(c => c.SkipActionIds)
            .GreaterThan(0)
            .WithMessage("SkipActionIds must contain positive integer action IDs.");

        RuleForEach(c => c.SpecificMachineIds)
            .Must(BePositiveIdString)
            .WithMessage("SpecificMachineIds must contain positive integer IDs.");

        RuleForEach(c => c.ExcludedMachineIds)
            .Must(BePositiveIdString)
            .WithMessage("ExcludedMachineIds must contain positive integer IDs.");

        RuleFor(c => c)
            .Must(HaveValidQueueWindow)
            .WithMessage("QueueTimeExpiry must be later than QueueTime.");
    }

    private static bool BePositiveIdString(string raw)
    {
        return !string.IsNullOrWhiteSpace(raw)
               && int.TryParse(raw.Trim(), out var parsed)
               && parsed > 0;
    }

    private static bool HaveValidQueueWindow(ValidateDeploymentEnvironmentRequest request)
    {
        if (!request.QueueTime.HasValue && request.QueueTimeExpiry.HasValue)
            return false;

        if (!request.QueueTime.HasValue || !request.QueueTimeExpiry.HasValue)
            return true;

        return request.QueueTimeExpiry.Value > request.QueueTime.Value;
    }
}
