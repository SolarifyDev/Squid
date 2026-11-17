using Squid.Core.Services.Deployments.ServerTask.Exceptions;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Squid.Api.Filters;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using Squid.Message.Response;
using Squid.UnitTests.Services.OctopusImport;

namespace Squid.UnitTests.Filters;

public class GlobalExceptionFilterTests
{
    [Fact]
    public void OnException_WhenOctopusImportSessionIsMissing_ReturnsNotFoundEnvelopeWithHttp200()
    {
        var exception = new OctopusImportSessionNotFoundException(Guid.NewGuid());

        var context = Execute(exception);

        AssertEnvelope(context, HttpStatusCode.NotFound, exception.Message);
    }

    [Fact]
    public void OnException_WhenOctopusImportStateTransitionIsInvalid_ReturnsConflictEnvelopeWithHttp200()
    {
        var exception = new OctopusImportSessionStateTransitionException(
            OctopusImportSessionState.Uploaded,
            OctopusImportSessionState.Succeeded);

        var context = Execute(exception);

        AssertEnvelope(context, HttpStatusCode.Conflict, exception.Message);
    }

    [Fact]
    public void OnException_WhenOctopusImportUpdateIsConcurrent_ReturnsConflictEnvelopeWithHttp200()
    {
        var exception = new OctopusImportSessionConcurrencyException(Guid.NewGuid());

        var context = Execute(exception);

        AssertEnvelope(context, HttpStatusCode.Conflict, exception.Message);
    }

    // A session past its maximum age conflicts with the request like any other closed session: start a new one.
    [Fact]
    public void OnException_ImportSessionPastMaxAge_ReturnsConflictEnvelope()
    {
        var exception = new OctopusImportSessionExpiredException(Guid.NewGuid(), DateTimeOffset.UtcNow, TimeSpan.FromHours(168));

        var context = Execute(exception);

        AssertEnvelope(context, HttpStatusCode.Conflict, exception.Message);
    }

    [Theory]
    [InlineData(true, HttpStatusCode.BadRequest)]
    [InlineData(false, HttpStatusCode.Conflict)]
    public void OnException_ProjectSelectionRefused_InvalidIsBadRequest_BeyondReadIsConflict(bool invalid, HttpStatusCode expected)
    {
        Exception exception = invalid ? new OctopusImportProjectSelectionInvalidException(OctopusImportProjectSelectionProblem.TooMany, "too many") : new OctopusImportProjectSelectionBeyondReadException(Guid.NewGuid());

        var context = Execute(exception);

        AssertEnvelope(context, expected, exception.Message);
    }

    public static IEnumerable<object[]> ServerSourceFailures()
    {
        foreach (var kind in Enum.GetValues<OctopusRestErrorKind>())
            yield return [new OctopusRestException(kind)];

        yield return [new OctopusImportSessionNotServerSourceException(Guid.NewGuid())];
        yield return [new OctopusImportHistoryServerMismatchException("server")];
        yield return [new OctopusImportApiKeyRestrictedException(new OctopusServerPermissionsDto { OctopusSpaceId = "Spaces-1", Restricted = ["VariableView"] })];
    }

    // A bad Octopus key is the caller's input error, never a Squid authentication failure (401 would sign the user out).
    [Theory]
    [MemberData(nameof(ServerSourceFailures))]
    public void OnException_WhenTheOctopusServerSourceFails_ReturnsBadRequestEnvelope(Exception exception)
    {
        var context = Execute(exception);

        AssertEnvelope(context, HttpStatusCode.BadRequest, exception.Message);
    }

    // A full disk on this Squid server is not the caller's input error: the envelope says so (507) with a fixed message that never
    // carries the server's file system layout.
    [Fact]
    public void OnException_ImportStorageFailure_InsufficientStorageEnvelope_NoPath()
    {
        var exception = new OctopusImportStorageException();

        var context = Execute(exception);

        AssertEnvelope(context, HttpStatusCode.InsufficientStorage, exception.Message);
        Response(context).Msg.ShouldNotContain("/");
    }

    // The import page decides how to recover (resume, reconnect, start again) from errorKind, never from the message text.
    [Theory]
    [MemberData(nameof(OctopusImportErrorKindsTests.ImportFailures), MemberType = typeof(OctopusImportErrorKindsTests))]
    public void OnException_ImportFailure_CarriesItsErrorKind(Exception exception, string expectedKind)
    {
        var context = Execute(exception);

        Response(context).ErrorKind.ShouldBe(expectedKind);
    }

    [Theory]
    [MemberData(nameof(OctopusImportErrorKindsTests.OtherFailures), MemberType = typeof(OctopusImportErrorKindsTests))]
    public void OnException_OtherFailure_HasNoErrorKind(Exception exception)
    {
        var context = Execute(exception);

        Response(context).ErrorKind.ShouldBeNull();
    }

    [Fact]
    public void ErrorKind_SerializedAsErrorKind()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new SquidResponse { ErrorKind = "Unreachable" });

        json.ShouldContain("\"errorKind\":\"Unreachable\"", Case.Sensitive);
    }

    private static SquidResponse Response(ExceptionContext context) => context.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<SquidResponse>();

    private static ExceptionContext Execute(Exception exception)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Get;
        httpContext.Request.Path = "/api/octopus-import/session";
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor(),
            new ModelStateDictionary());
        var exceptionContext = new ExceptionContext(actionContext, [])
        {
            Exception = exception
        };

        new GlobalExceptionFilter().OnException(exceptionContext);
        return exceptionContext;
    }

    private static void AssertEnvelope(
        ExceptionContext context,
        HttpStatusCode expectedCode,
        string expectedMessage)
    {
        context.ExceptionHandled.ShouldBeTrue();
        var result = context.Result.ShouldBeOfType<OkObjectResult>();
        result.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var response = result.Value.ShouldBeOfType<SquidResponse>();
        response.Code.ShouldBe(expectedCode);
        response.Msg.ShouldBe(expectedMessage);
    }

    [Fact]
    public void ImportedReadOnly_409()
    {
        var exception = new ImportedServerTaskReadOnlyException(42);

        var context = Execute(exception);

        AssertEnvelope(context, HttpStatusCode.Conflict, exception.Message);
    }
}
