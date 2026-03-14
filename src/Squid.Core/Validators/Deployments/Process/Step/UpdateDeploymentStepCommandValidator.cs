using FluentValidation;
using Squid.Message.Commands.Deployments.Process.Step;

namespace Squid.Core.Validators.Deployments.Process.Step;

public class UpdateDeploymentStepCommandValidator : FluentMessageValidator<UpdateDeploymentStepCommand>
{
    public UpdateDeploymentStepCommandValidator()
    {
        RuleFor(c => c.Id).GreaterThan(0);

        RuleFor(c => c.Step).NotNull();

        RuleFor(c => c.Step.Name).NotEmpty().MaximumLength(200)
            .When(c => c.Step != null);

        RuleForEach(c => c.Step.Actions).ChildRules(action =>
        {
            action.RuleFor(a => a.ActionType).NotEmpty();
        }).When(c => c.Step != null);
    }
}
