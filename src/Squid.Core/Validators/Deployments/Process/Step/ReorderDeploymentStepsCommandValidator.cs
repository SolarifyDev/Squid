using FluentValidation;
using Squid.Message.Commands.Deployments.Process.Step;

namespace Squid.Core.Validators.Deployments.Process.Step;

public class ReorderDeploymentStepsCommandValidator : FluentMessageValidator<ReorderDeploymentStepsCommand>
{
    public ReorderDeploymentStepsCommandValidator()
    {
        RuleFor(c => c.ProcessId).GreaterThan(0);

        RuleFor(c => c.StepOrders).NotEmpty();

        RuleForEach(c => c.StepOrders).ChildRules(item =>
        {
            item.RuleFor(i => i.StepId).GreaterThan(0);
            item.RuleFor(i => i.StepOrder).GreaterThan(0);
        }).When(c => c.StepOrders != null);

        RuleFor(c => c.StepOrders)
            .Must(items => items.Select(i => i.StepId).Distinct().Count() == items.Count)
            .WithMessage("Duplicate StepId detected")
            .When(c => c.StepOrders is { Count: > 0 });

        RuleFor(c => c.StepOrders)
            .Must(items => items.Select(i => i.StepOrder).Distinct().Count() == items.Count)
            .WithMessage("Duplicate StepOrder detected")
            .When(c => c.StepOrders is { Count: > 0 });
    }
}
