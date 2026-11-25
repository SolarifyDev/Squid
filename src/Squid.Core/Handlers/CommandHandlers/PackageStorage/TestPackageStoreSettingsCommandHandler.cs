using Squid.Core.Services.Packages.Storage;
using Squid.Message.Commands.PackageStorage;

namespace Squid.Core.Handlers.CommandHandlers.PackageStorage;

public class TestPackageStoreSettingsCommandHandler(IPackageStoreService service) : ICommandHandler<TestPackageStoreSettingsCommand, TestPackageStoreSettingsResponse>
{
    public async Task<TestPackageStoreSettingsResponse> Handle(IReceiveContext<TestPackageStoreSettingsCommand> context, CancellationToken cancellationToken)
    {
        var result = await service.TestSettingsAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new TestPackageStoreSettingsResponse { Data = result };
    }
}
