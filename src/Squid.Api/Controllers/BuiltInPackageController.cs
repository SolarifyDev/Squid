using Microsoft.AspNetCore.Authorization;
using Squid.Message.Commands.BuiltInPackages;
using Squid.Message.Commands.Publishing;
using Squid.Message.Requests.BuiltInPackages;
using Squid.Message.Requests.Publishing;

namespace Squid.Api.Controllers;

/// <summary>
/// A space's built-in package feed. Pushing needs <c>FeedEdit</c> and reading needs <c>FeedView</c>, enforced by the <c>[RequiresPermission]</c> attribute on each
/// command and request in the Mediator pipeline.
/// </summary>
[ApiController]
[Authorize]
[Route("api/builtin-feed")]
public class BuiltInPackageController(IMediator mediator) : ControllerBase
{
    /// <summary>
    /// Pushes a package. The request body is the file itself (not multipart), read as a stream into a temp file, so a large package never sits in memory;
    /// the other fields travel in the query string, for example <c>?spaceId=1&amp;fileName=Acme.Web.1.2.3.zip</c>. The body limit is lifted here because the push service
    /// enforces the package size limit itself, as it reads.
    /// </summary>
    [HttpPost("packages/push")]
    [DisableRequestSizeLimit]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PushBuiltInPackageResponse))]
    public async Task<IActionResult> PushBuiltInPackageAsync([FromQuery] PushBuiltInPackageCommand command, CancellationToken ct)
    {
        command.Content = Request.Body;
        command.DeclaredSizeBytes = Request.ContentLength;

        var response = await mediator.SendAsync<PushBuiltInPackageCommand, PushBuiltInPackageResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("packages")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ListBuiltInPackagesResponse))]
    public async Task<IActionResult> ListBuiltInPackagesAsync([FromQuery] ListBuiltInPackagesRequest request, CancellationToken ct)
    {
        var response = await mediator.RequestAsync<ListBuiltInPackagesRequest, ListBuiltInPackagesResponse>(request, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("packages/versions")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ListBuiltInPackageVersionsResponse))]
    public async Task<IActionResult> ListBuiltInPackageVersionsAsync([FromQuery] ListBuiltInPackageVersionsRequest request, CancellationToken ct)
    {
        var response = await mediator.RequestAsync<ListBuiltInPackageVersionsRequest, ListBuiltInPackageVersionsResponse>(request, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("packages/version")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetBuiltInPackageVersionResponse))]
    public async Task<IActionResult> GetBuiltInPackageVersionAsync([FromQuery] GetBuiltInPackageVersionRequest request, CancellationToken ct)
    {
        var response = await mediator.RequestAsync<GetBuiltInPackageVersionRequest, GetBuiltInPackageVersionResponse>(request, ct).ConfigureAwait(false);

        return Ok(response);
    }

    /// <summary>The recipes that fit a stored version, best first with their reasons, and the facts inspection found in it.</summary>
    [HttpGet("packages/version/recipes")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetRecipeRecommendationsResponse))]
    public async Task<IActionResult> GetRecipeRecommendationsAsync([FromQuery] GetRecipeRecommendationsRequest request, CancellationToken ct)
    {
        var response = await mediator.RequestAsync<GetRecipeRecommendationsRequest, GetRecipeRecommendationsResponse>(request, ct).ConfigureAwait(false);

        return Ok(response);
    }

    /// <summary>
    /// Publishes a stored version as an app: picks the recipe, creates (or reuses) the package's project and process, creates the release and starts the deployment.
    /// Needs <c>FeedView</c>, <c>ProjectCreate</c>, <c>ProcessEdit</c>, <c>ReleaseCreate</c> and <c>DeploymentCreate</c>.
    /// </summary>
    [HttpPost("packages/publish")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PublishPackageResponse))]
    public async Task<IActionResult> PublishPackageAsync([FromBody] PublishPackageCommand command, CancellationToken ct)
    {
        var response = await mediator.SendAsync<PublishPackageCommand, PublishPackageResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }
}
