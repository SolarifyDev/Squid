using Squid.Core.Services.Packages.Storage;
using Squid.Message.Requests.PackageStorage;

namespace Squid.Core.Handlers.RequestHandlers.PackageStorage;

public class ListPackageStoreProvidersRequestHandler(IPackageStoreService service) : IRequestHandler<ListPackageStoreProvidersRequest, ListPackageStoreProvidersResponse>
{
    public Task<ListPackageStoreProvidersResponse> Handle(IReceiveContext<ListPackageStoreProvidersRequest> context, CancellationToken cancellationToken)
    {
        return Task.FromResult(new ListPackageStoreProvidersResponse { Data = service.DescribeProviders() });
    }
}
