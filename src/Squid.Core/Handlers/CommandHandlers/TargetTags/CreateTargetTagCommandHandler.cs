using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.TargetTags;
using Squid.Message.Commands.TargetTag;
using Squid.Message.Events.TargetTag;
using Squid.Message.Models.Deployments.TargetTag;

namespace Squid.Core.Handlers.CommandHandlers.TargetTags;

public class CreateTargetTagCommandHandler : ICommandHandler<CreateTargetTagCommand, CreateTargetTagResponse>
{
    private readonly IMapper _mapper;
    private readonly ITargetTagDataProvider _dataProvider;

    public CreateTargetTagCommandHandler(IMapper mapper, ITargetTagDataProvider dataProvider)
    {
        _mapper = mapper;
        _dataProvider = dataProvider;
    }

    public async Task<CreateTargetTagResponse> Handle(IReceiveContext<CreateTargetTagCommand> context, CancellationToken cancellationToken)
    {
        var tag = _mapper.Map<TargetTag>(context.Message);

        await _dataProvider.AddAsync(tag, cancellationToken: cancellationToken).ConfigureAwait(false);

        var dto = _mapper.Map<TargetTagDto>(tag);

        var @event = new TargetTagCreatedEvent { Data = dto };
        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new CreateTargetTagResponse
        {
            Data = new CreateTargetTagResponseData
            {
                TargetTag = dto
            }
        };
    }
}
