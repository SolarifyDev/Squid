using Squid.Core.Services.Packages.Versioning;
using Squid.Message.Requests.PackageVersioning;

namespace Squid.Core.Handlers.RequestHandlers.PackageVersioning;

public class ListPackageVersionRulesRequestHandler(IPackageVersionRuleService rules) : IRequestHandler<ListPackageVersionRulesRequest, ListPackageVersionRulesResponse>
{
    public async Task<ListPackageVersionRulesResponse> Handle(IReceiveContext<ListPackageVersionRulesRequest> context, CancellationToken cancellationToken)
    {
        var request = context.Message;
        var list = await rules.ListRulesAsync(request.SpaceId, request.FeedId, cancellationToken).ConfigureAwait(false);

        return new ListPackageVersionRulesResponse { Data = new ListPackageVersionRulesResponseData { Rules = list } };
    }
}
