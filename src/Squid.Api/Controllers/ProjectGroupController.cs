using Squid.Message.Commands.Deployments.ProjectGroup;
using Squid.Message.Requests.Deployments.ProjectGroup;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/project-groups")]
public class ProjectGroupController : ControllerBase
{
    private readonly IMediator _mediator;

    public ProjectGroupController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("create")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateProjectGroupResponse))]
    public async Task<IActionResult> CreateProjectGroupAsync([FromBody] CreateProjectGroupCommand command)
    {
        var response = await _mediator.SendAsync<CreateProjectGroupCommand, CreateProjectGroupResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("update")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateProjectGroupResponse))]
    public async Task<IActionResult> UpdateProjectGroupAsync([FromBody] UpdateProjectGroupCommand command)
    {
        var response = await _mediator.SendAsync<UpdateProjectGroupCommand, UpdateProjectGroupResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("delete")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteProjectGroupsResponse))]
    public async Task<IActionResult> DeleteProjectGroupsAsync([FromBody] DeleteProjectGroupsCommand command)
    {
        var response = await _mediator.SendAsync<DeleteProjectGroupsCommand, DeleteProjectGroupsResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("list")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetProjectGroupsResponse))]
    public async Task<IActionResult> GetProjectGroupsAsync([FromQuery] GetProjectGroupsRequest request)
    {
        var response = await _mediator.RequestAsync<GetProjectGroupsRequest, GetProjectGroupsResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }
}
