using Squid.Core.Services.Packages.Storage;
using Squid.Message.Commands.PackageStorage;

namespace Squid.Core.Handlers.CommandHandlers.PackageStorage;

public class SetDefaultPackageStoreCommandHandler(IPackageStoreService service) : ICommandHandler<SetDefaultPackageStoreCommand, SetDefaultPackageStoreResponse>
{
    public async Task<SetDefaultPackageStoreResponse> Handle(IReceiveContext<SetDefaultPackageStoreCommand> context, CancellationToken cancellationToken)
    {
        var store = await service.SetDefaultAsync(context.Message.Id, cancellationToken).ConfigureAwait(false);

        return new SetDefaultPackageStoreResponse { Data = store };
    }
}
