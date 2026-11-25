using Squid.Core.Services.Packages.Versioning;
using Squid.Message.Requests.PackageVersioning;

namespace Squid.Core.Handlers.RequestHandlers.PackageVersioning;

public class GetPackageVersionRuleRequestHandler(IPackageVersionRuleService rules) : IRequestHandler<GetPackageVersionRuleRequest, GetPackageVersionRuleResponse>
{
    public async Task<GetPackageVersionRuleResponse> Handle(IReceiveContext<GetPackageVersionRuleRequest> context, CancellationToken cancellationToken)
    {
        var request = context.Message;
        var rule = await rules.GetEffectiveRuleAsync(request.SpaceId, request.FeedId, request.PackageId, cancellationToken).ConfigureAwait(false);

        return new GetPackageVersionRuleResponse { Data = rule };
    }
}
