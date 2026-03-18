using Squid.Message.Requests.Deployments.Process;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/deployment-processes")]
public class DeploymentProcessController : ControllerBase
{
    private readonly IMediator _mediator;

    public DeploymentProcessController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet("list")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetDeploymentProcessesResponse))]
    public async Task<IActionResult> GetDeploymentProcessesAsync([FromQuery] GetDeploymentProcessesRequest request)
    {
        var response = await _mediator.RequestAsync<GetDeploymentProcessesRequest, GetDeploymentProcessesResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("detail/{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetDeploymentProcessResponse))]
    public async Task<IActionResult> GetDeploymentProcessAsync(int id, [FromQuery] int? spaceId = null)
    {
        var request = new GetDeploymentProcessRequest { Id = id, SpaceId = spaceId };
        var response = await _mediator.RequestAsync<GetDeploymentProcessRequest, GetDeploymentProcessResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("{projectId:int}/package-references")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetPackageReferencesResponse))]
    public async Task<IActionResult> GetPackageReferencesAsync(int projectId, [FromQuery] int? spaceId = null)
    {
        var request = new GetPackageReferencesRequest { ProjectId = projectId, SpaceId = spaceId };
        var response = await _mediator.RequestAsync<GetPackageReferencesRequest, GetPackageReferencesResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }
}
