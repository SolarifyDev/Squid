using FluentValidation;
using Squid.Core.Middlewares.FluentMessageValidator;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Validators.Machines;

public class RegisterKubernetesAgentCommandValidator : FluentMessageValidator<RegisterKubernetesAgentCommand>
{
    public RegisterKubernetesAgentCommandValidator()
    {
        RuleFor(c => c.Thumbprint).NotEmpty();
        RuleFor(c => c.SubscriptionId).NotEmpty();
    }
}
