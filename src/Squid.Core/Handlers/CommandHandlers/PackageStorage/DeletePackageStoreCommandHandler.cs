using Squid.Core.Services.Packages.Storage;
using Squid.Message.Commands.PackageStorage;

namespace Squid.Core.Handlers.CommandHandlers.PackageStorage;

public class DeletePackageStoreCommandHandler(IPackageStoreService service) : ICommandHandler<DeletePackageStoreCommand, DeletePackageStoreResponse>
{
    public async Task<DeletePackageStoreResponse> Handle(IReceiveContext<DeletePackageStoreCommand> context, CancellationToken cancellationToken)
    {
        await service.DeleteAsync(context.Message.Id, cancellationToken).ConfigureAwait(false);

        return new DeletePackageStoreResponse();
    }
}
