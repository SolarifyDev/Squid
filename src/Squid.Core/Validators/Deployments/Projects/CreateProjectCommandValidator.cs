using FluentValidation;
using Squid.Core.Middlewares.FluentMessageValidator;
using Squid.Message.Commands.Deployments.Project;

namespace Squid.Core.Validators.Deployments.Projects;

public class CreateProjectCommandValidator : FluentMessageValidator<CreateProjectCommand>
{
    public CreateProjectCommandValidator()
    {
        RuleFor(c => c.Project).NotNull();

        RuleFor(c => c.Project.Name).NotEmpty().MaximumLength(200)
            .When(c => c.Project != null);

        RuleFor(c => c.Project.LifecycleId).GreaterThan(0)
            .When(c => c.Project != null);

        RuleFor(c => c.Project.ProjectGroupId).GreaterThan(0)
            .When(c => c.Project != null);
    }
}
