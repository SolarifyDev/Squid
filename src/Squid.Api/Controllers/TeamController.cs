using Squid.Message.Commands.Teams;
using Squid.Message.Requests.Teams;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/teams")]
public class TeamController : ControllerBase
{
    private readonly IMediator _mediator;

    public TeamController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("create")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateTeamResponse))]
    public async Task<IActionResult> CreateAsync([FromBody] CreateTeamCommand command)
    {
        var response = await _mediator.SendAsync<CreateTeamCommand, CreateTeamResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("update")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateTeamResponse))]
    public async Task<IActionResult> UpdateAsync([FromBody] UpdateTeamCommand command)
    {
        var response = await _mediator.SendAsync<UpdateTeamCommand, UpdateTeamResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("delete")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteTeamResponse))]
    public async Task<IActionResult> DeleteAsync([FromBody] DeleteTeamCommand command)
    {
        var response = await _mediator.SendAsync<DeleteTeamCommand, DeleteTeamResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("list")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetTeamsResponse))]
    public async Task<IActionResult> GetAllAsync([FromQuery] GetTeamsRequest request)
    {
        var response = await _mediator.RequestAsync<GetTeamsRequest, GetTeamsResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("{id:int}/members/add")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(AddTeamMemberResponse))]
    public async Task<IActionResult> AddMemberAsync(int id, [FromBody] AddTeamMemberCommand command)
    {
        command.TeamId = id;

        var response = await _mediator.SendAsync<AddTeamMemberCommand, AddTeamMemberResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("{id:int}/members/remove")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(RemoveTeamMemberResponse))]
    public async Task<IActionResult> RemoveMemberAsync(int id, [FromBody] RemoveTeamMemberCommand command)
    {
        command.TeamId = id;

        var response = await _mediator.SendAsync<RemoveTeamMemberCommand, RemoveTeamMemberResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("{id:int}/members")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetTeamMembersResponse))]
    public async Task<IActionResult> GetMembersAsync(int id)
    {
        var response = await _mediator.RequestAsync<GetTeamMembersRequest, GetTeamMembersResponse>(new GetTeamMembersRequest { TeamId = id }).ConfigureAwait(false);

        return Ok(response);
    }
}
