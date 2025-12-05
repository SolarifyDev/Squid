using Microsoft.AspNetCore.Mvc;
using Squid.Message.Commands.Deployments.Process.Step;
using Squid.Message.Requests.Deployments.Process.Step;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DeploymentStepController : ControllerBase
{
    private readonly IMediator _mediator;

    public DeploymentStepController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateDeploymentStepResponse))]
    public async Task<IActionResult> CreateDeploymentStepAsync([FromBody] CreateDeploymentStepCommand command)
    {
        var response = await _mediator.SendAsync<CreateDeploymentStepCommand, CreateDeploymentStepResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateDeploymentStepResponse))]
    public async Task<IActionResult> UpdateDeploymentStepAsync([FromBody] UpdateDeploymentStepCommand command)
    {
        var response = await _mediator.SendAsync<UpdateDeploymentStepCommand, UpdateDeploymentStepResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteDeploymentStepResponse))]
    public async Task<IActionResult> DeleteDeploymentStepsAsync([FromBody] DeleteDeploymentStepCommand command)
    {
        var response = await _mediator.SendAsync<DeleteDeploymentStepCommand, DeleteDeploymentStepResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetDeploymentStepsResponse))]
    public async Task<IActionResult> GetDeploymentStepsAsync([FromQuery] GetDeploymentStepsRequest request)
    {
        var response = await _mediator.RequestAsync<GetDeploymentStepsRequest, GetDeploymentStepsResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetDeploymentStepResponse))]
    public async Task<IActionResult> GetDeploymentStepAsync(int id)
    {
        var request = new GetDeploymentStepRequest { Id = id };
        var response = await _mediator.RequestAsync<GetDeploymentStepRequest, GetDeploymentStepResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }
}

