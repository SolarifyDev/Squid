using Squid.Message.Commands.TargetTag;
using Squid.Message.Requests.TargetTag;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/target-tags")]
public class TargetTagController : ControllerBase
{
    private readonly IMediator _mediator;

    public TargetTagController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("create")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateTargetTagResponse))]
    public async Task<IActionResult> CreateTargetTagAsync([FromBody] CreateTargetTagCommand command)
    {
        var response = await _mediator.SendAsync<CreateTargetTagCommand, CreateTargetTagResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("delete")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteTargetTagsResponse))]
    public async Task<IActionResult> DeleteTargetTagsAsync([FromBody] DeleteTargetTagsCommand command)
    {
        var response = await _mediator.SendAsync<DeleteTargetTagsCommand, DeleteTargetTagsResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("list")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetTargetTagsResponse))]
    public async Task<IActionResult> GetTargetTagsAsync([FromQuery] GetTargetTagsRequest request)
    {
        var response = await _mediator.RequestAsync<GetTargetTagsRequest, GetTargetTagsResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }
}
