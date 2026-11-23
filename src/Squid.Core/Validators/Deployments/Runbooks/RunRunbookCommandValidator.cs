using FluentValidation;
using Squid.Message.Commands.Deployments.Runbook;

namespace Squid.Core.Validators.Deployments.Runbooks;

public class RunRunbookCommandValidator : FluentMessageValidator<RunRunbookCommand>
{
    public RunRunbookCommandValidator()
    {
        RuleFor(command => command.Id).GreaterThan(0);
        RuleFor(command => command.ProjectId).GreaterThan(0);
        RuleFor(command => command.SpaceId).GreaterThan(0)
            .When(command => command.SpaceId.HasValue);
        RuleFor(command => command.EnvironmentId).GreaterThan(0);
    }
}
