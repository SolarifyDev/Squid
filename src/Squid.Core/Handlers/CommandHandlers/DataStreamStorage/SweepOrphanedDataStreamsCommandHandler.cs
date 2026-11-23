using Squid.Core.Halibut.DataStreamStorage;
using Squid.Message.Commands.DataStreamStorage;

namespace Squid.Core.Handlers.CommandHandlers.DataStreamStorage;

public class SweepOrphanedDataStreamsCommandHandler(IDataStreamOrphanSweeper sweeper) : ICommandHandler<SweepOrphanedDataStreamsCommand, SweepOrphanedDataStreamsResponse>
{
    public async Task<SweepOrphanedDataStreamsResponse> Handle(IReceiveContext<SweepOrphanedDataStreamsCommand> context, CancellationToken cancellationToken)
    {
        var removed = await sweeper.SweepAsync(cancellationToken).ConfigureAwait(false);

        return new SweepOrphanedDataStreamsResponse { Data = new SweepOrphanedDataStreamsResponseData { Removed = removed } };
    }
}
