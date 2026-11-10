using FluentValidation;
using Squid.Message.Commands.Deployments.Runbook;

namespace Squid.Core.Validators.Deployments.Runbooks;

public class CopyRunbookCommandValidator : FluentMessageValidator<CopyRunbookCommand>
{
    public CopyRunbookCommandValidator()
    {
        RuleFor(c => c.SourceRunbookId).GreaterThan(0);
        RuleFor(c => c.ProjectId).GreaterThan(0);
        RuleFor(c => c.SpaceId).GreaterThan(0)
            .When(c => c.SpaceId.HasValue);
        RuleFor(c => c.Name).MaximumLength(200)
            .When(c => !string.IsNullOrWhiteSpace(c.Name));
    }
}
