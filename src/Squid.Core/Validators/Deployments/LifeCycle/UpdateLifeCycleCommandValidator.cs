using FluentValidation;
using Squid.Core.Middlewares.FluentMessageValidator;
using Squid.Message.Commands.Deployments.LifeCycle;

namespace Squid.Core.Validators.Deployments.LifeCycle;

public class UpdateLifeCycleCommandValidator : FluentMessageValidator<UpdateLifeCycleCommand>
{
    public UpdateLifeCycleCommandValidator()
    {
        RuleFor(c => c.LifecyclePhase).NotNull();

        RuleFor(c => c.LifecyclePhase.Lifecycle).NotNull()
            .When(c => c.LifecyclePhase != null);

        RuleFor(c => c.LifecyclePhase.Lifecycle.Name).NotEmpty().MaximumLength(200)
            .When(c => c.LifecyclePhase?.Lifecycle != null);

        RuleForEach(c => c.LifecyclePhase.Phases)
            .ChildRules(phase => phase.RuleFor(p => p.Name).NotEmpty().MaximumLength(200))
            .When(c => c.LifecyclePhase?.Phases != null);
    }
}
