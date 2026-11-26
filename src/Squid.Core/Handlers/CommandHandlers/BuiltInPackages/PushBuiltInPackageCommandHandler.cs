using Squid.Core.Services.Packages.BuiltIn;
using Squid.Message.Commands.BuiltInPackages;

namespace Squid.Core.Handlers.CommandHandlers.BuiltInPackages;

public class PushBuiltInPackageCommandHandler(IBuiltInPackagePushService push) : ICommandHandler<PushBuiltInPackageCommand, PushBuiltInPackageResponse>
{
    public async Task<PushBuiltInPackageResponse> Handle(IReceiveContext<PushBuiltInPackageCommand> context, CancellationToken cancellationToken)
    {
        var data = await push.PushAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new PushBuiltInPackageResponse { Data = data };
    }
}
