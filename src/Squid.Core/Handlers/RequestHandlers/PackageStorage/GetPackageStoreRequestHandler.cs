using Squid.Core.Services.Packages.Storage;
using Squid.Message.Requests.PackageStorage;

namespace Squid.Core.Handlers.RequestHandlers.PackageStorage;

public class GetPackageStoreRequestHandler(IPackageStoreService service) : IRequestHandler<GetPackageStoreRequest, GetPackageStoreResponse>
{
    public async Task<GetPackageStoreResponse> Handle(IReceiveContext<GetPackageStoreRequest> context, CancellationToken cancellationToken)
    {
        var store = await service.GetAsync(context.Message.Id, cancellationToken).ConfigureAwait(false);

        return new GetPackageStoreResponse { Data = store };
    }
}
