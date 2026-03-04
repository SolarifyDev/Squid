using FluentValidation;
using Squid.Core.Middlewares.FluentMessageValidator;
using Squid.Message.Commands.Deployments.Process.Step;

namespace Squid.Core.Validators.Deployments.Process.Step;

public class CreateDeploymentStepCommandValidator : FluentMessageValidator<CreateDeploymentStepCommand>
{
    public CreateDeploymentStepCommandValidator()
    {
        RuleFor(c => c.ProcessId).GreaterThan(0);

        RuleFor(c => c.Step).NotNull();

        RuleFor(c => c.Step.Name).NotEmpty().MaximumLength(200)
            .When(c => c.Step != null);

        RuleForEach(c => c.Step.Actions).ChildRules(action =>
        {
            action.RuleFor(a => a.ActionType).NotEmpty();
        }).When(c => c.Step != null);
    }
}
