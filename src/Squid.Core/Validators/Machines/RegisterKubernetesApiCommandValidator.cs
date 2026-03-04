using FluentValidation;
using Squid.Core.Middlewares.FluentMessageValidator;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Validators.Machines;

public class RegisterKubernetesApiCommandValidator : FluentMessageValidator<RegisterKubernetesApiCommand>
{
    public RegisterKubernetesApiCommandValidator()
    {
        RuleFor(c => c.ClusterUrl).NotEmpty();
        RuleFor(c => c.ResourceReferences).NotNull()
            .Must(r => r != null && r.Count > 0)
            .WithMessage("At least one resource reference is required");
    }
}
