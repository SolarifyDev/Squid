using Squid.Message.Commands.Deployments.Deployment;
using Squid.Message.Requests.Deployments.Deployment;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/deployments")]
public class DeploymentController : ControllerBase
{
    private readonly IMediator _mediator;

    public DeploymentController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("create")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateDeploymentResponse))]
    public async Task<IActionResult> CreateDeploymentAsync([FromBody] CreateDeploymentCommand command)
    {
        var response = await _mediator.SendAsync<CreateDeploymentCommand, CreateDeploymentResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("preview")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PreviewDeploymentResponse))]
    public async Task<IActionResult> PreviewDeploymentAsync([FromBody] PreviewDeploymentRequest request)
    {
        var response = await _mediator.RequestAsync<PreviewDeploymentRequest, PreviewDeploymentResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetDeploymentResponse))]
    public async Task<IActionResult> GetDeploymentAsync(int id, [FromQuery] bool? verbose = null, [FromQuery] int? tail = null)
    {
        var request = new GetDeploymentRequest { Id = id, Verbose = verbose, Tail = tail };

        var response = await _mediator.RequestAsync<GetDeploymentRequest, GetDeploymentResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }
}
