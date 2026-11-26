using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Squid.Api.Filters;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds.Exceptions;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Core.Services.Packages.Storage.Exceptions;
using Squid.Message.Response;

namespace Squid.UnitTests.Services.Packages.BuiltIn;

/// <summary>Every refusal of the built-in feed reaches the client with its own status, not as a 500.</summary>
public class BuiltInExceptionStatusTests
{
    public static IEnumerable<object[]> Cases()
    {
        yield return [new BuiltInFeedNotFoundException(1), HttpStatusCode.NotFound];
        yield return [new BuiltInPackageNotFoundException(1, "p", null), HttpStatusCode.NotFound];
        yield return [new BuiltInPackageNotFoundException(1, "p", "1.0.0"), HttpStatusCode.NotFound];
        yield return [new PackageTooLargeException(2147483648), HttpStatusCode.RequestEntityTooLarge];
        yield return [new EmptyPackageUploadException(), HttpStatusCode.BadRequest];
        yield return [new UnsupportedPackageFormatException(), HttpStatusCode.BadRequest];
        yield return [new PackageArchiveRejectedException("zip-slip", null), HttpStatusCode.BadRequest];
        yield return [new PackageIdRequiredException("x.zip"), HttpStatusCode.BadRequest];
        yield return [new InvalidPackageIdException("a b", "spaces"), HttpStatusCode.BadRequest];
        yield return [new PackageVersionAlreadyExistsException("p", "1.0.0"), HttpStatusCode.Conflict];
        yield return [new PackageVersionInUseException("p", "1.0.0"), HttpStatusCode.Conflict];
        yield return [new BuiltInFeedStoreInvalidException(1, "x"), HttpStatusCode.BadRequest];
        yield return [new BuiltInPackageTooLargeToDeployException("p", "1", 1), HttpStatusCode.Conflict];
        yield return [new PackageStoreInUseException(1), HttpStatusCode.Conflict];
        yield return [new BuiltInPackageIntegrityException("p", "1", "a", "b"), HttpStatusCode.InternalServerError];
        yield return [new PackageArchiveLimitExceededException("has more than 3 entries"), HttpStatusCode.RequestEntityTooLarge];
        yield return [new BuiltInFeedConflictException(1, 2, "https://x"), HttpStatusCode.Conflict];
        yield return [new BuiltInFeedDeletionException(1), HttpStatusCode.Conflict];
        yield return [new BuiltInFeedTypeChangeException(1, "BuiltIn", "NuGet"), HttpStatusCode.Conflict];
        yield return [new BuiltInFeedCreationException(), HttpStatusCode.Conflict];
        yield return [new Squid.Core.Services.Packages.Versioning.Exceptions.NonSortablePackageVersionException("{input}", "banana", "x"), HttpStatusCode.BadRequest];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Exception_MapsToItsStatus(Exception exception, HttpStatusCode expected)
    {
        var context = Execute(exception);

        var response = ((OkObjectResult)context.Result).Value.ShouldBeOfType<SquidResponse>();

        response.Code.ShouldBe(expected);
        response.Msg.ShouldBe(exception.Message);
    }

    private static ExceptionContext Execute(Exception exception)
    {
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor(), new ModelStateDictionary());
        var context = new ExceptionContext(actionContext, []) { Exception = exception };

        new GlobalExceptionFilter().OnException(context);

        return context;
    }
}
