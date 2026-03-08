using FluentValidation;
using Squid.Core.Middlewares.FluentMessageValidator;
using Squid.Message.Commands.Deployments.Deployment;

namespace Squid.Core.Validators.Deployments.Deployment;

public class CreateDeploymentCommandValidator : FluentMessageValidator<CreateDeploymentCommand>
{
    public CreateDeploymentCommandValidator()
    {
        RuleFor(c => c.ReleaseId).GreaterThan(0);
        RuleFor(c => c.EnvironmentId).GreaterThan(0);

        RuleFor(c => c.Name)
            .MaximumLength(200)
            .When(c => !string.IsNullOrWhiteSpace(c.Name));

        RuleForEach(c => c.SpecificMachineIds)
            .Must(BePositiveIntString)
            .WithMessage("SpecificMachineIds must contain positive integer machine IDs.");

        RuleForEach(c => c.ExcludedMachineIds)
            .Must(BePositiveIntString)
            .WithMessage("ExcludedMachineIds must contain positive integer machine IDs.");

        RuleForEach(c => c.SkipActionIds)
            .GreaterThan(0)
            .WithMessage("SkipActionIds must contain positive integer action IDs.");

        RuleFor(c => c)
            .Must(HasNoOverlap)
            .WithMessage("SpecificMachineIds and ExcludedMachineIds cannot overlap.");

        RuleFor(c => c)
            .Must(HasValidQueueWindow)
            .WithMessage("QueueTimeExpiry must be later than QueueTime.");
    }

    private static bool BePositiveIntString(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && int.TryParse(value.Trim(), out var parsed)
               && parsed > 0;
    }

    private static bool HasNoOverlap(CreateDeploymentCommand command)
    {
        var specific = command.SpecificMachineIds
            .Where(v => int.TryParse(v, out _))
            .Select(int.Parse)
            .ToHashSet();

        var excluded = command.ExcludedMachineIds
            .Where(v => int.TryParse(v, out _))
            .Select(int.Parse)
            .ToHashSet();

        return !specific.Overlaps(excluded);
    }

    private static bool HasValidQueueWindow(CreateDeploymentCommand command)
    {
        if (!command.QueueTime.HasValue && command.QueueTimeExpiry.HasValue)
            return false;

        if (!command.QueueTime.HasValue || !command.QueueTimeExpiry.HasValue)
            return true;

        return command.QueueTimeExpiry.Value > command.QueueTime.Value;
    }
}
