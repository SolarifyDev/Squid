using Squid.Core.Services.Publishing;
using Squid.Message.Commands.Publishing;

namespace Squid.Core.Handlers.CommandHandlers.Publishing;

public class PublishPackageCommandHandler(IPublishService publishing) : ICommandHandler<PublishPackageCommand, PublishPackageResponse>
{
    public async Task<PublishPackageResponse> Handle(IReceiveContext<PublishPackageCommand> context, CancellationToken cancellationToken)
    {
        var data = await publishing.PublishAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new PublishPackageResponse { Data = data };
    }
}
