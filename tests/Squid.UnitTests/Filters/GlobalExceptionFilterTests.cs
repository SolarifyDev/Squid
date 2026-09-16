using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Squid.Api.Filters;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Response;

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
}
