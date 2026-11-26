using Squid.Core.Services.Packages.BuiltIn;
using Squid.Message.Requests.BuiltInPackages;

namespace Squid.Core.Handlers.RequestHandlers.BuiltInPackages;

public class ListBuiltInPackagesRequestHandler(IBuiltInPackageQueryService query) : IRequestHandler<ListBuiltInPackagesRequest, ListBuiltInPackagesResponse>
{
    public async Task<ListBuiltInPackagesResponse> Handle(IReceiveContext<ListBuiltInPackagesRequest> context, CancellationToken cancellationToken)
    {
        var data = await query.ListPackagesAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new ListBuiltInPackagesResponse { Data = data };
    }
}
