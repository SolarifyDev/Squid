using Microsoft.AspNetCore.Mvc;
using Squid.Message.Commands.Deployments.Project;
using Squid.Message.Requests.Deployments.Project;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProjectController : ControllerBase
{
    private readonly IMediator _mediator;

    public ProjectController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateProjectResponse))]
    public async Task<IActionResult> CreateProjectAsync([FromBody] CreateProjectCommand command)
    {
        var response = await _mediator.SendAsync<CreateProjectCommand, CreateProjectResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateProjectResponse))]
    public async Task<IActionResult> UpdateProjectAsync([FromBody] UpdateProjectCommand command)
    {
        var response = await _mediator.SendAsync<UpdateProjectCommand, UpdateProjectResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteProjectsResponse))]
    public async Task<IActionResult> DeleteProjectsAsync([FromBody] DeleteProjectsCommand command)
    {
        var response = await _mediator.SendAsync<DeleteProjectsCommand, DeleteProjectsResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetProjectsResponse))]
    public async Task<IActionResult> GetProjectsAsync([FromQuery] GetProjectsRequest request)
    {
        var response = await _mediator.RequestAsync<GetProjectsRequest, GetProjectsResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetProjectResponse))]
    public async Task<IActionResult> GetProjectAsync(int id)
    {
        var request = new GetProjectRequest { Id = id };
        var response = await _mediator.RequestAsync<GetProjectRequest, GetProjectResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }
}

