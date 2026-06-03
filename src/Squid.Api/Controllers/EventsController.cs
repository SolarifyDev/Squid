using Squid.Message.Requests.Events;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/events")]
public class EventsController : ControllerBase
{
    private readonly IMediator _mediator;

    public EventsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet("list")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetEventsResponse))]
    public async Task<IActionResult> GetEventsAsync([FromQuery] GetEventsRequest request)
    {
        var response = await _mediator.RequestAsync<GetEventsRequest, GetEventsResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }
}
