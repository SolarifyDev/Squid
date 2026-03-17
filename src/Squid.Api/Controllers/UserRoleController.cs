using Squid.Message.Commands.Authorization;
using Squid.Message.Requests.Authorization;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/userroles")]
public class UserRoleController : ControllerBase
{
    private readonly IMediator _mediator;

    public UserRoleController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet("list")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetUserRolesResponse))]
    public async Task<IActionResult> GetAllAsync()
    {
        var response = await _mediator.RequestAsync<GetUserRolesRequest, GetUserRolesResponse>(new GetUserRolesRequest()).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetUserRoleResponse))]
    public async Task<IActionResult> GetByIdAsync(int id)
    {
        var response = await _mediator.RequestAsync<GetUserRoleRequest, GetUserRoleResponse>(new GetUserRoleRequest { Id = id }).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("create")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateUserRoleResponse))]
    public async Task<IActionResult> CreateAsync([FromBody] CreateUserRoleCommand command)
    {
        var response = await _mediator.SendAsync<CreateUserRoleCommand, CreateUserRoleResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("update")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateUserRoleResponse))]
    public async Task<IActionResult> UpdateAsync([FromBody] UpdateUserRoleCommand command)
    {
        var response = await _mediator.SendAsync<UpdateUserRoleCommand, UpdateUserRoleResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("delete")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteUserRoleResponse))]
    public async Task<IActionResult> DeleteAsync([FromBody] DeleteUserRoleCommand command)
    {
        var response = await _mediator.SendAsync<DeleteUserRoleCommand, DeleteUserRoleResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }
}
