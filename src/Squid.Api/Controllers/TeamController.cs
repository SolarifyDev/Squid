using Squid.Message.Commands.Authorization;
using Squid.Message.Commands.Teams;
using Squid.Message.Requests.Authorization;
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

    [HttpGet("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetTeamResponse))]
    public async Task<IActionResult> GetByIdAsync(int id)
    {
        var response = await _mediator.RequestAsync<GetTeamRequest, GetTeamResponse>(new GetTeamRequest { Id = id }).ConfigureAwait(false);

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

    [HttpGet("{id:int}/roles")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetTeamRolesResponse))]
    public async Task<IActionResult> GetRolesAsync(int id)
    {
        var response = await _mediator.RequestAsync<GetTeamRolesRequest, GetTeamRolesResponse>(new GetTeamRolesRequest { TeamId = id }).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("{id:int}/roles/assign")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(AssignRoleToTeamResponse))]
    public async Task<IActionResult> AssignRoleAsync(int id, [FromBody] AssignRoleToTeamCommand command)
    {
        command.TeamId = id;

        var response = await _mediator.SendAsync<AssignRoleToTeamCommand, AssignRoleToTeamResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("{id:int}/roles/remove")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(RemoveRoleFromTeamResponse))]
    public async Task<IActionResult> RemoveRoleAsync(int id, [FromBody] RemoveRoleFromTeamCommand command)
    {
        command.TeamId = id;

        var response = await _mediator.SendAsync<RemoveRoleFromTeamCommand, RemoveRoleFromTeamResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("{id:int}/roles/{scopedRoleId:int}/scope")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateRoleScopeResponse))]
    public async Task<IActionResult> UpdateRoleScopeAsync(int id, int scopedRoleId, [FromBody] UpdateRoleScopeCommand command)
    {
        command.TeamId = id;
        command.ScopedUserRoleId = scopedRoleId;

        var response = await _mediator.SendAsync<UpdateRoleScopeCommand, UpdateRoleScopeResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }
}
