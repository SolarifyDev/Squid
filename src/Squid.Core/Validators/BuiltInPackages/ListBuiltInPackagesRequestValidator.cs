using FluentValidation;
using Squid.Message.Requests.BuiltInPackages;

namespace Squid.Core.Validators.BuiltInPackages;

public class ListBuiltInPackagesRequestValidator : FluentMessageValidator<ListBuiltInPackagesRequest>
{
    public const int MaxTake = 500;

    public ListBuiltInPackagesRequestValidator()
    {
        RuleFor(r => r.Take).InclusiveBetween(1, MaxTake);
    }
}
