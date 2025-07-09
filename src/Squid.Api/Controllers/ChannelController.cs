using Squid.Message.Commands.Deployments.Channel;
using Squid.Message.Requests.Deployments.Channel;

namespace Squid.Api.Controllers;

public class ChannelController : ControllerBase
{
    private readonly IMediator _mediator;

    public ChannelController(IMediator mediator)
    {
        _mediator = mediator;
    }
    
    [Route("create"), HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateChannelResponse))]
    public async Task<IActionResult> CreateChannelAsync([FromBody] CreateChannelCommand command)
    {
        var response = await _mediator.SendAsync<CreateChannelCommand, CreateChannelResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }
    
    [Route("update"), HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateChannelResponse))]
    public async Task<IActionResult> UpdateChannelAsync([FromBody] UpdateChannelCommand command)
    {
        var response = await _mediator.SendAsync<UpdateChannelCommand, UpdateChannelResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }
    
    [Route("delete"), HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteChannelsResponse))]
    public async Task<IActionResult> DeleteChannelsAsync([FromBody] DeleteChannelsCommand command)
    {
        var response = await _mediator.SendAsync<DeleteChannelsCommand, DeleteChannelsResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }
    
    [Route("list"), HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetChannelsResponse))]
    public async Task<IActionResult> GetChannelsAsync([FromQuery] GetChannelsRequest request)
    {
        var response = await _mediator.RequestAsync<GetChannelsRequest, GetChannelsResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }
}