using FluentValidation;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Validators.Machines;

public class RegisterTentacleListeningCommandValidator : FluentMessageValidator<RegisterTentacleListeningCommand>
{
    public RegisterTentacleListeningCommandValidator()
    {
        RuleFor(c => c.Uri).NotEmpty();
        RuleFor(c => c.Thumbprint).NotEmpty();
    }
}
