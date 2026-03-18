using Squid.Message.Commands.Deployments.Project;
using Squid.Message.Requests.Deployments.Project;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/projects")]
public class ProjectController : ControllerBase
{
    private readonly IMediator _mediator;

    public ProjectController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("create")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateProjectResponse))]
    public async Task<IActionResult> CreateProjectAsync([FromBody] CreateProjectCommand command)
    {
        var response = await _mediator.SendAsync<CreateProjectCommand, CreateProjectResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("update")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateProjectResponse))]
    public async Task<IActionResult> UpdateProjectAsync([FromBody] UpdateProjectCommand command)
    {
        var response = await _mediator.SendAsync<UpdateProjectCommand, UpdateProjectResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("delete")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteProjectsResponse))]
    public async Task<IActionResult> DeleteProjectsAsync([FromBody] DeleteProjectsCommand command)
    {
        var response = await _mediator.SendAsync<DeleteProjectsCommand, DeleteProjectsResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("list")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetProjectsResponse))]
    public async Task<IActionResult> GetProjectsAsync([FromQuery] GetProjectsRequest request)
    {
        var response = await _mediator.RequestAsync<GetProjectsRequest, GetProjectsResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("detail/{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetProjectResponse))]
    public async Task<IActionResult> GetProjectAsync(int id, [FromQuery] int? spaceId = null)
    {
        var request = new GetProjectRequest { Id = id, SpaceId = spaceId };
        var response = await _mediator.RequestAsync<GetProjectRequest, GetProjectResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("summaries")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetProjectSummariesResponse))]
    public async Task<IActionResult> GetProjectSummariesAsync([FromQuery] GetProjectSummariesRequest request)
    {
        var response = await _mediator.RequestAsync<GetProjectSummariesRequest, GetProjectSummariesResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("{projectId:int}/progression")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetProjectProgressionResponse))]
    public async Task<IActionResult> GetProjectProgressionAsync(int projectId, [FromQuery] int? spaceId = null)
    {
        var request = new GetProjectProgressionRequest { ProjectId = projectId, SpaceId = spaceId };
        var response = await _mediator.RequestAsync<GetProjectProgressionRequest, GetProjectProgressionResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }
}
