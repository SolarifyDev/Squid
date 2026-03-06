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

    [HttpPost("validate-environment")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ValidateDeploymentEnvironmentResponse))]
    public async Task<IActionResult> ValidateDeploymentEnvironmentAsync([FromBody] ValidateDeploymentEnvironmentRequest request)
    {
        var response = await _mediator
            .RequestAsync<ValidateDeploymentEnvironmentRequest, ValidateDeploymentEnvironmentResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }
}
