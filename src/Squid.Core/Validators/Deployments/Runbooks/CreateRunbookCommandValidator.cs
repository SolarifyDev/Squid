using FluentValidation;
using Squid.Message.Commands.Deployments.Runbook;

namespace Squid.Core.Validators.Deployments.Runbooks;

public class CreateRunbookCommandValidator : FluentMessageValidator<CreateRunbookCommand>
{
    public CreateRunbookCommandValidator()
    {
        RuleFor(c => c.Runbook).NotNull();

        RuleFor(c => c.Runbook.ProjectId).GreaterThan(0)
            .When(c => c.Runbook != null);

        RuleFor(c => c.Runbook.SpaceId).GreaterThan(0)
            .When(c => c.Runbook != null);

        RuleFor(c => c.Runbook.Name).NotEmpty().MaximumLength(200)
            .When(c => c.Runbook != null);

        RuleFor(c => c.Runbook.Description).MaximumLength(4000)
            .When(c => c.Runbook != null);

        RuleFor(c => c.Runbook.Tags).Must(tags => tags == null || tags.Count <= 50)
            .WithMessage("A Runbook can have at most 50 tags.")
            .When(c => c.Runbook != null);

        RuleForEach(c => c.Runbook.Tags)
            .Must(tag => tag == null || tag.Trim().Length <= 100)
            .WithMessage("Runbook tags must be at most 100 characters.")
            .When(c => c.Runbook != null && c.Runbook.Tags != null);
    }
}
