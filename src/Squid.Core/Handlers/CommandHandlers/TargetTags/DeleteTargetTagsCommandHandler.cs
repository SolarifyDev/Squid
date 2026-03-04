using Squid.Core.Services.TargetTags;
using Squid.Message.Commands.TargetTag;
using Squid.Message.Events.TargetTag;

namespace Squid.Core.Handlers.CommandHandlers.TargetTags;

public class DeleteTargetTagsCommandHandler : ICommandHandler<DeleteTargetTagsCommand, DeleteTargetTagsResponse>
{
    private readonly ITargetTagDataProvider _dataProvider;

    public DeleteTargetTagsCommandHandler(ITargetTagDataProvider dataProvider)
    {
        _dataProvider = dataProvider;
    }

    public async Task<DeleteTargetTagsResponse> Handle(IReceiveContext<DeleteTargetTagsCommand> context, CancellationToken cancellationToken)
    {
        var tags = await _dataProvider.GetByIdsAsync(context.Message.Ids, cancellationToken).ConfigureAwait(false);

        await _dataProvider.DeleteAsync(tags, cancellationToken: cancellationToken).ConfigureAwait(false);

        var deletedIds = tags.Select(t => t.Id).ToList();
        var failIds = context.Message.Ids.Except(deletedIds).ToList();

        var @event = new TargetTagDeletedEvent
        {
            Data = new DeleteTargetTagsResponseData { FailIds = failIds }
        };
        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new DeleteTargetTagsResponse
        {
            Data = @event.Data
        };
    }
}
