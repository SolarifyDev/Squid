using FluentValidation;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Validators.Machines;

public class RegisterLinuxPollingCommandValidator : FluentMessageValidator<RegisterLinuxPollingCommand>
{
    public RegisterLinuxPollingCommandValidator()
    {
        RuleFor(c => c.Thumbprint).NotEmpty();
        RuleFor(c => c.SubscriptionId).NotEmpty();
    }
}
