using FluentValidation;
using Squid.Message.Requests.Deployments.Deployment;

namespace Squid.Core.Validators.Deployments.Deployment;

public class PreviewDeploymentRequestValidator : FluentMessageValidator<PreviewDeploymentRequest>
{
    public PreviewDeploymentRequestValidator()
    {
        RuleFor(request => request.DeploymentRequestPayload)
            .NotNull();

        When(request => request.DeploymentRequestPayload != null, () =>
        {
            RuleFor(request => request.DeploymentRequestPayload.ReleaseId)
                .GreaterThan(0);

            RuleFor(request => request.DeploymentRequestPayload.EnvironmentId)
                .GreaterThan(0);

            RuleForEach(request => request.DeploymentRequestPayload.SkipActionIds)
                .GreaterThan(0)
                .WithMessage("SkipActionIds must contain positive integer action IDs.");

            RuleForEach(request => request.DeploymentRequestPayload.SpecificMachineIds)
                .GreaterThan(0)
                .WithMessage("SpecificMachineIds must contain positive integer IDs.");

            RuleForEach(request => request.DeploymentRequestPayload.ExcludedMachineIds)
                .GreaterThan(0)
                .WithMessage("ExcludedMachineIds must contain positive integer IDs.");

            RuleFor(request => request.DeploymentRequestPayload)
                .Must(HaveValidQueueWindow)
                .WithMessage("QueueTimeExpiry must be later than QueueTime.");

            RuleFor(request => request.DeploymentRequestPayload)
                .Must(HaveNoMachineOverlap)
                .WithMessage("SpecificMachineIds and ExcludedMachineIds cannot overlap.");
        });
    }

    private static bool HaveValidQueueWindow(Message.Models.Deployments.Deployment.DeploymentRequestPayload payload)
    {
        if (!payload.QueueTime.HasValue && payload.QueueTimeExpiry.HasValue)
            return false;

        if (!payload.QueueTime.HasValue || !payload.QueueTimeExpiry.HasValue)
            return true;

        return payload.QueueTimeExpiry.Value > payload.QueueTime.Value;
    }

    private static bool HaveNoMachineOverlap(Message.Models.Deployments.Deployment.DeploymentRequestPayload payload)
    {
        var specific = payload.SpecificMachineIds
            .Where(id => id > 0)
            .ToHashSet();

        var excluded = payload.ExcludedMachineIds
            .Where(id => id > 0)
            .ToHashSet();

        return !specific.Overlaps(excluded);
    }
}
