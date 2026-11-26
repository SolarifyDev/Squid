using Microsoft.AspNetCore.Authorization;
using Squid.Message.Commands.PackageStorage;
using Squid.Message.Requests.PackageStorage;

namespace Squid.Api.Controllers;

/// <summary>
/// Where uploaded packages are stored. Server-wide, so every action requires the <c>AdministerSystem</c> permission, enforced by the
/// <c>[RequiresPermission]</c> attribute on each command and request in the Mediator pipeline.
/// </summary>
[ApiController]
[Authorize]
[Route("api/package-stores")]
public class PackageStoreController(IMediator mediator) : ControllerBase
{
    [HttpGet("list")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ListPackageStoresResponse))]
    public async Task<IActionResult> ListPackageStoresAsync([FromQuery] ListPackageStoresRequest request, CancellationToken ct)
    {
        var response = await mediator.RequestAsync<ListPackageStoresRequest, ListPackageStoresResponse>(request, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("providers")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ListPackageStoreProvidersResponse))]
    public async Task<IActionResult> ListPackageStoreProvidersAsync([FromQuery] ListPackageStoreProvidersRequest request, CancellationToken ct)
    {
        var response = await mediator.RequestAsync<ListPackageStoreProvidersRequest, ListPackageStoreProvidersResponse>(request, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetPackageStoreResponse))]
    public async Task<IActionResult> GetPackageStoreAsync([FromRoute] int id, CancellationToken ct)
    {
        var response = await mediator.RequestAsync<GetPackageStoreRequest, GetPackageStoreResponse>(new GetPackageStoreRequest { Id = id }, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("create")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreatePackageStoreResponse))]
    public async Task<IActionResult> CreatePackageStoreAsync([FromBody] CreatePackageStoreCommand command, CancellationToken ct)
    {
        var response = await mediator.SendAsync<CreatePackageStoreCommand, CreatePackageStoreResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("update")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdatePackageStoreResponse))]
    public async Task<IActionResult> UpdatePackageStoreAsync([FromBody] UpdatePackageStoreCommand command, CancellationToken ct)
    {
        var response = await mediator.SendAsync<UpdatePackageStoreCommand, UpdatePackageStoreResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("delete")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeletePackageStoreResponse))]
    public async Task<IActionResult> DeletePackageStoreAsync([FromBody] DeletePackageStoreCommand command, CancellationToken ct)
    {
        var response = await mediator.SendAsync<DeletePackageStoreCommand, DeletePackageStoreResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("set-default")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(SetDefaultPackageStoreResponse))]
    public async Task<IActionResult> SetDefaultPackageStoreAsync([FromBody] SetDefaultPackageStoreCommand command, CancellationToken ct)
    {
        var response = await mediator.SendAsync<SetDefaultPackageStoreCommand, SetDefaultPackageStoreResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("test")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(TestPackageStoreResponse))]
    public async Task<IActionResult> TestPackageStoreAsync([FromBody] TestPackageStoreCommand command, CancellationToken ct)
    {
        var response = await mediator.SendAsync<TestPackageStoreCommand, TestPackageStoreResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }

    /// <summary>Tests settings that are not saved yet (the "Test" button on a form); writes nothing.</summary>
    [HttpPost("test-settings")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(TestPackageStoreSettingsResponse))]
    public async Task<IActionResult> TestPackageStoreSettingsAsync([FromBody] TestPackageStoreSettingsCommand command, CancellationToken ct)
    {
        var response = await mediator.SendAsync<TestPackageStoreSettingsCommand, TestPackageStoreSettingsResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }
}
