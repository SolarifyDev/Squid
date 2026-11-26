using Squid.Core.Services.Packages.BuiltIn;
using Squid.Message.Requests.BuiltInPackages;

namespace Squid.Core.Handlers.RequestHandlers.BuiltInPackages;

public class ListBuiltInPackageVersionsRequestHandler(IBuiltInPackageQueryService query) : IRequestHandler<ListBuiltInPackageVersionsRequest, ListBuiltInPackageVersionsResponse>
{
    public async Task<ListBuiltInPackageVersionsResponse> Handle(IReceiveContext<ListBuiltInPackageVersionsRequest> context, CancellationToken cancellationToken)
    {
        var data = await query.ListVersionsAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new ListBuiltInPackageVersionsResponse { Data = data };
    }
}
