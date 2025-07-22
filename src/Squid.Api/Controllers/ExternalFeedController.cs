using Microsoft.AspNetCore.Mvc;
using Squid.Message.Commands.Deployments.ExternalFeed;
using Squid.Message.Requests.Deployments.ExternalFeed;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ExternalFeedController : ControllerBase
{
    private readonly IMediator _mediator;

    public ExternalFeedController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateExternalFeedResponse))]
    public async Task<IActionResult> CreateExternalFeedAsync([FromBody] CreateExternalFeedCommand command)
    {
        var response = await _mediator.SendAsync<CreateExternalFeedCommand, CreateExternalFeedResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateExternalFeedResponse))]
    public async Task<IActionResult> UpdateExternalFeedAsync([FromBody] UpdateExternalFeedCommand command)
    {
        var response = await _mediator.SendAsync<UpdateExternalFeedCommand, UpdateExternalFeedResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteExternalFeedsResponse))]
    public async Task<IActionResult> DeleteExternalFeedsAsync([FromBody] DeleteExternalFeedsCommand command)
    {
        var response = await _mediator.SendAsync<DeleteExternalFeedsCommand, DeleteExternalFeedsResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetExternalFeedsResponse))]
    public async Task<IActionResult> GetExternalFeedsAsync([FromQuery] GetExternalFeedsRequest request)
    {
        var response = await _mediator.RequestAsync<GetExternalFeedsRequest, GetExternalFeedsResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }
}
