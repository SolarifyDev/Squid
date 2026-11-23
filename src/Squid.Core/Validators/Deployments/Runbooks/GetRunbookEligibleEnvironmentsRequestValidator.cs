using FluentValidation;
using Squid.Message.Requests.Deployments.Runbook;

namespace Squid.Core.Validators.Deployments.Runbooks;

public class GetRunbookEligibleEnvironmentsRequestValidator
    : FluentMessageValidator<GetRunbookEligibleEnvironmentsRequest>
{
    public GetRunbookEligibleEnvironmentsRequestValidator()
    {
        RuleFor(request => request.Id).GreaterThan(0);
        RuleFor(request => request.ProjectId).GreaterThan(0);
        RuleFor(request => request.SpaceId).GreaterThan(0).When(request => request.SpaceId.HasValue);
    }
}
