using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Squid.Message.Commands.Account;
using Squid.Message.Constants;
using Squid.Message.Requests.Account;
using Squid.Message.Response;

namespace Squid.Api.Controllers;

[ApiController]
[Route("auth")]
public class AccountController : ControllerBase
{
    private readonly IMediator _mediator;

    public AccountController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [AllowAnonymous]
    [HttpPost("register")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(RegisterResponse))]
    public async Task<IActionResult> RegisterAsync([FromBody] RegisterCommand command, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _mediator.SendAsync<RegisterCommand, RegisterResponse>(command, cancellationToken).ConfigureAwait(false);
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new SquidResponse { Code = HttpStatusCode.BadRequest, Msg = ex.Message });
        }
    }

    [AllowAnonymous]
    [HttpPost("login")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(LoginResponse))]
    public async Task<IActionResult> LoginAsync([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _mediator.RequestAsync<LoginRequest, LoginResponse>(request, cancellationToken).ConfigureAwait(false);
            return Ok(response);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new SquidResponse { Code = HttpStatusCode.Unauthorized, Msg = ex.Message });
        }
    }

    [Authorize]
    [HttpGet("me")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetCurrentUserResponse))]
    public async Task<IActionResult> MeAsync(CancellationToken cancellationToken)
    {
        var idValue = User.Claims.FirstOrDefault(x => x.Type == ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(idValue, out var userId))
        {
            return Unauthorized(new SquidResponse { Code = HttpStatusCode.Unauthorized, Msg = "Invalid token" });
        }

        var response = await _mediator.RequestAsync<GetCurrentUserRequest, GetCurrentUserResponse>(
            new GetCurrentUserRequest { UserId = userId }, cancellationToken).ConfigureAwait(false);

        if (response.Data == null)
        {
            return Unauthorized(new SquidResponse { Code = HttpStatusCode.Unauthorized, Msg = "User not found" });
        }

        return Ok(response);
    }
}
