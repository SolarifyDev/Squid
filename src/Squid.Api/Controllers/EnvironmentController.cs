using Microsoft.AspNetCore.Mvc;
using Squid.Message.Commands.Deployments.Environment;
using Squid.Message.Requests.Deployments.Environment;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EnvironmentController : ControllerBase
{
    private readonly IMediator _mediator;

    public EnvironmentController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateEnvironmentResponse))]
    public async Task<IActionResult> CreateEnvironmentAsync([FromBody] CreateEnvironmentCommand command)
    {
        var response = await _mediator.SendAsync<CreateEnvironmentCommand, CreateEnvironmentResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateEnvironmentResponse))]
    public async Task<IActionResult> UpdateEnvironmentAsync([FromBody] UpdateEnvironmentCommand command)
    {
        var response = await _mediator.SendAsync<UpdateEnvironmentCommand, UpdateEnvironmentResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteEnvironmentsResponse))]
    public async Task<IActionResult> DeleteEnvironmentsAsync([FromBody] DeleteEnvironmentsCommand command)
    {
        var response = await _mediator.SendAsync<DeleteEnvironmentsCommand, DeleteEnvironmentsResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetEnvironmentsResponse))]
    public async Task<IActionResult> GetEnvironmentsAsync([FromQuery] GetEnvironmentsRequest request)
    {
        var response = await _mediator.RequestAsync<GetEnvironmentsRequest, GetEnvironmentsResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }
}
