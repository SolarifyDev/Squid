using Squid.Core.Services.Events;
using Squid.Message.Requests.Events;

namespace Squid.Core.Handlers.RequestHandlers.Events;

public sealed class GetEventsRequestHandler : IRequestHandler<GetEventsRequest, GetEventsResponse>
{
    private readonly IEventService _eventService;

    public GetEventsRequestHandler(IEventService eventService)
    {
        _eventService = eventService;
    }

    public async Task<GetEventsResponse> Handle(IReceiveContext<GetEventsRequest> context, CancellationToken cancellationToken)
    {
        var data = await _eventService.GetEventsAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new GetEventsResponse { Data = data };
    }
}
