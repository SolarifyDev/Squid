using FluentValidation;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Validators.Machines;

public class RegisterTentaclePollingCommandValidator : FluentMessageValidator<RegisterTentaclePollingCommand>
{
    public RegisterTentaclePollingCommandValidator()
    {
        RuleFor(c => c.Thumbprint).NotEmpty();
        RuleFor(c => c.SubscriptionId).NotEmpty();
    }
}
