using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Message.Commands.Deployments.ExternalFeed;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.ExternalFeed;

public class TestExternalFeedCommandHandler(IExternalFeedConnectionTestService connectionTestService) : ICommandHandler<TestExternalFeedCommand, TestExternalFeedResponse>
{
    public async Task<TestExternalFeedResponse> Handle(IReceiveContext<TestExternalFeedCommand> context, CancellationToken cancellationToken)
    {
        var result = await connectionTestService
            .TestAsync(context.Message.Id, cancellationToken).ConfigureAwait(false);

        return new TestExternalFeedResponse { Data = result };
    }
}
