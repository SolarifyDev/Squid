using FluentValidation;
using Squid.Message.Commands.Deployments.Release;

namespace Squid.Core.Validators.Deployments.Release;

public class CreateReleaseCommandValidator : FluentMessageValidator<CreateReleaseCommand>
{
    public CreateReleaseCommandValidator()
    {
        RuleFor(c => c.Version).NotEmpty().MaximumLength(349);
        RuleFor(c => c.ProjectId).GreaterThan(0);
        RuleFor(c => c.ChannelId).GreaterThan(0);

        RuleForEach(c => c.SelectedPackages).ChildRules(pkg =>
        {
            pkg.RuleFor(x => x.ActionName).NotEmpty();
            pkg.RuleFor(x => x.Version).NotEmpty();
        });
    }
}
