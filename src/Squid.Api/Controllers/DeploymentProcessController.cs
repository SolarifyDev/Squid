using Microsoft.AspNetCore.Mvc;
using Squid.Message.Commands.Deployments.Process;
using Squid.Message.Requests.Deployments.Process;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DeploymentProcessController : ControllerBase
{
    private readonly IMediator _mediator;

    public DeploymentProcessController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateDeploymentProcessResponse))]
    public async Task<IActionResult> CreateDeploymentProcessAsync([FromBody] CreateDeploymentProcessCommand command)
    {
        var response = await _mediator.SendAsync<CreateDeploymentProcessCommand, CreateDeploymentProcessResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateDeploymentProcessResponse))]
    public async Task<IActionResult> UpdateDeploymentProcessAsync([FromBody] UpdateDeploymentProcessCommand command)
    {
        var response = await _mediator.SendAsync<UpdateDeploymentProcessCommand, UpdateDeploymentProcessResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetDeploymentProcessesResponse))]
    public async Task<IActionResult> GetDeploymentProcessesAsync([FromQuery] GetDeploymentProcessesRequest request)
    {
        var response = await _mediator.RequestAsync<GetDeploymentProcessesRequest, GetDeploymentProcessesResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetDeploymentProcessResponse))]
    public async Task<IActionResult> GetDeploymentProcessAsync(int id)
    {
        var request = new GetDeploymentProcessRequest { Id = id };
        var response = await _mediator.RequestAsync<GetDeploymentProcessRequest, GetDeploymentProcessResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteDeploymentProcessResponse))]
    public async Task<IActionResult> DeleteDeploymentProcessAsync([FromBody] DeleteDeploymentProcessCommand command)
    {
        var response = await _mediator.SendAsync<DeleteDeploymentProcessCommand, DeleteDeploymentProcessResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }
}
