using FluentValidation;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Validators.Machines;

public class RegisterLinuxListeningCommandValidator : FluentMessageValidator<RegisterLinuxListeningCommand>
{
    public RegisterLinuxListeningCommandValidator()
    {
        RuleFor(c => c.Uri).NotEmpty();
        RuleFor(c => c.Thumbprint).NotEmpty();
    }
}
