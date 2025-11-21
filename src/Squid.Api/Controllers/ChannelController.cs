using Squid.Core.Services.Common;
using Squid.Message.Commands.Deployments.Channel;
using Squid.Message.Requests.Deployments.Channel;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChannelController : ControllerBase
{
    private readonly IMediator _mediator;

    public ChannelController(IMediator mediator)
    {
        _mediator = mediator;
    }
    
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateChannelResponse))]
    public async Task<IActionResult> CreateChannelAsync([FromBody] CreateChannelCommand command)
    {
        var response = await _mediator.SendAsync<CreateChannelCommand, CreateChannelResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }
    
    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateChannelResponse))]
    public async Task<IActionResult> UpdateChannelAsync([FromBody] UpdateChannelCommand command)
    {
        var response = await _mediator.SendAsync<UpdateChannelCommand, UpdateChannelResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }
    
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteChannelsResponse))]
    public async Task<IActionResult> DeleteChannelsAsync([FromBody] DeleteChannelsCommand command)
    {
        var response = await _mediator.SendAsync<DeleteChannelsCommand, DeleteChannelsResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }
    
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetChannelsResponse))]
    public async Task<IActionResult> GetChannelsAsync([FromQuery] GetChannelsRequest request)
    {
        var response = await _mediator.RequestAsync<GetChannelsRequest, GetChannelsResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }
    
    [HttpPost]
    [Route("test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> TestsAsync([FromBody] testClass request)
    {
        var client = new DockerHubClient(request.Username, request.Password);
        var response = await client.DownloadPrivateImageAsync(request.ImageName, "/download").ConfigureAwait(false);

        return Ok(response);
    }
    
    public class testClass
    {
        public string Username { get; set; } = string.Empty;
        
        public string Password { get; set; } = string.Empty;
        
        public string ImageName { get; set; } = string.Empty;
    }
}