using Squid.Message.Commands.Account;
using Squid.Message.Requests.Account;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/users")]
public class UserController : ControllerBase
{
    private readonly IMediator _mediator;

    public UserController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet("list")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetUsersResponse))]
    public async Task<IActionResult> GetAllAsync()
    {
        var response = await _mediator.RequestAsync<GetUsersRequest, GetUsersResponse>(new GetUsersRequest()).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("create")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateUserResponse))]
    public async Task<IActionResult> CreateAsync([FromBody] CreateUserCommand command)
    {
        var response = await _mediator.SendAsync<CreateUserCommand, CreateUserResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }
}
