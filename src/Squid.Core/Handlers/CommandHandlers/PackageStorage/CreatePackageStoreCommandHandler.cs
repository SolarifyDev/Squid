using Squid.Core.Services.Packages.Storage;
using Squid.Message.Commands.PackageStorage;

namespace Squid.Core.Handlers.CommandHandlers.PackageStorage;

public class CreatePackageStoreCommandHandler(IPackageStoreService service) : ICommandHandler<CreatePackageStoreCommand, CreatePackageStoreResponse>
{
    public async Task<CreatePackageStoreResponse> Handle(IReceiveContext<CreatePackageStoreCommand> context, CancellationToken cancellationToken)
    {
        var store = await service.CreateAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new CreatePackageStoreResponse { Data = store };
    }
}
