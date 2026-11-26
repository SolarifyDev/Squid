using Squid.Core.Services.Packages.Storage;
using Squid.Message.Commands.PackageStorage;

namespace Squid.Core.Handlers.CommandHandlers.PackageStorage;

public class TestPackageStoreCommandHandler(IPackageStoreService service) : ICommandHandler<TestPackageStoreCommand, TestPackageStoreResponse>
{
    public async Task<TestPackageStoreResponse> Handle(IReceiveContext<TestPackageStoreCommand> context, CancellationToken cancellationToken)
    {
        var result = await service.TestAsync(context.Message.Id, cancellationToken).ConfigureAwait(false);

        return new TestPackageStoreResponse { Data = result };
    }
}
