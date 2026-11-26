using Squid.Core.Services.Packages.Storage;
using Squid.Message.Requests.PackageStorage;

namespace Squid.Core.Handlers.RequestHandlers.PackageStorage;

public class ListPackageStoresRequestHandler(IPackageStoreService service) : IRequestHandler<ListPackageStoresRequest, ListPackageStoresResponse>
{
    public async Task<ListPackageStoresResponse> Handle(IReceiveContext<ListPackageStoresRequest> context, CancellationToken cancellationToken)
    {
        var stores = await service.ListAsync(cancellationToken).ConfigureAwait(false);

        return new ListPackageStoresResponse { Data = stores };
    }
}
