using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Squid.Message.Commands.Account;
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
    [HttpPost("login")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(LoginResponse))]
    public async Task<IActionResult> LoginAsync([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var response = await _mediator.RequestAsync<LoginRequest, LoginResponse>(request, cancellationToken).ConfigureAwait(false);

        return Ok(response);
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

    [Authorize]
    [HttpPost("change-password")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ChangePasswordResponse))]
    public async Task<IActionResult> ChangePasswordAsync([FromBody] ChangePasswordCommand command, CancellationToken cancellationToken)
    {
        var response = await _mediator.SendAsync<ChangePasswordCommand, ChangePasswordResponse>(command, cancellationToken).ConfigureAwait(false);

        return Ok(response);
    }

    [Authorize]
    [HttpPost("users/{userId:int}/status")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateUserStatusResponse))]
    public async Task<IActionResult> UpdateUserStatusAsync(int userId, [FromBody] UpdateUserStatusCommand command, CancellationToken cancellationToken)
    {
        command.UserId = userId;

        var response = await _mediator.SendAsync<UpdateUserStatusCommand, UpdateUserStatusResponse>(command, cancellationToken).ConfigureAwait(false);

        return Ok(response);
    }

    [Authorize]
    [HttpPost("api-keys")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateApiKeyResponse))]
    public async Task<IActionResult> CreateApiKeyAsync([FromBody] CreateApiKeyCommand command, CancellationToken cancellationToken)
    {
        var response = await _mediator.SendAsync<CreateApiKeyCommand, CreateApiKeyResponse>(command, cancellationToken).ConfigureAwait(false);

        return Ok(response);
    }

    [Authorize]
    [HttpDelete("api-keys/{apiKeyId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteApiKeyResponse))]
    public async Task<IActionResult> DeleteApiKeyAsync(int apiKeyId, CancellationToken cancellationToken)
    {
        var command = new DeleteApiKeyCommand { ApiKeyId = apiKeyId };

        var response = await _mediator.SendAsync<DeleteApiKeyCommand, DeleteApiKeyResponse>(command, cancellationToken).ConfigureAwait(false);

        return Ok(response);
    }

    [Authorize]
    [HttpGet("api-keys")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetMyApiKeysResponse))]
    public async Task<IActionResult> GetMyApiKeysAsync(CancellationToken cancellationToken)
    {
        var response = await _mediator.RequestAsync<GetMyApiKeysRequest, GetMyApiKeysResponse>(new GetMyApiKeysRequest(), cancellationToken).ConfigureAwait(false);

        return Ok(response);
    }
}
