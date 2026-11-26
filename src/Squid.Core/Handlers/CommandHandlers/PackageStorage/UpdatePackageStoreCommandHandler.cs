using Squid.Core.Services.Packages.Storage;
using Squid.Message.Commands.PackageStorage;

namespace Squid.Core.Handlers.CommandHandlers.PackageStorage;

public class UpdatePackageStoreCommandHandler(IPackageStoreService service) : ICommandHandler<UpdatePackageStoreCommand, UpdatePackageStoreResponse>
{
    public async Task<UpdatePackageStoreResponse> Handle(IReceiveContext<UpdatePackageStoreCommand> context, CancellationToken cancellationToken)
    {
        var store = await service.UpdateAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new UpdatePackageStoreResponse { Data = store };
    }
}
