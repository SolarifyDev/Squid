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

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetDeploymentProcessesResponse))]
    public async Task<IActionResult> GetDeploymentProcessesAsync([FromQuery] GetDeploymentProcessesRequest request)
    {
        var response = await _mediator.RequestAsync<GetDeploymentProcessesRequest, GetDeploymentProcessesResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetDeploymentProcessResponse))]
    public async Task<IActionResult> GetDeploymentProcessAsync(int id)
    {
        var request = new GetDeploymentProcessRequest { Id = id };
        var response = await _mediator.RequestAsync<GetDeploymentProcessRequest, GetDeploymentProcessResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }
}
