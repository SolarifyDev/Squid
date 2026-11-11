using FluentValidation;
using Squid.Message.Commands.Deployments.Runbook;

namespace Squid.Core.Validators.Deployments.Runbooks;

public class DeleteRunbookCommandValidator : FluentMessageValidator<DeleteRunbookCommand>
{
    public DeleteRunbookCommandValidator()
    {
        RuleFor(c => c.Id).GreaterThan(0);
        RuleFor(c => c.ProjectId).GreaterThan(0);
        RuleFor(c => c.SpaceId).GreaterThan(0)
            .When(c => c.SpaceId.HasValue);
    }
}
