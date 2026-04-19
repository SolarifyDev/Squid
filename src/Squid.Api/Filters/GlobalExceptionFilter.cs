using System.Net;
using FluentValidation;
using Microsoft.AspNetCore.Mvc.Filters;
using Serilog;
using Squid.Core.Services.Authorization.Exceptions;
using Squid.Core.Services.Machines.Exceptions;
using Squid.Message.Response;

namespace Squid.Api.Filters;

public class GlobalExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        var statusCode = context.Exception switch
        {
            ValidationException => HttpStatusCode.BadRequest,
            UnauthorizedAccessException => HttpStatusCode.Unauthorized,
            PermissionDeniedException => HttpStatusCode.Forbidden,
            MachineNotFoundException => HttpStatusCode.NotFound,
            MachineNameConflictException => HttpStatusCode.Conflict,
            _ => HttpStatusCode.InternalServerError
        };

        Log.Error(context.Exception, "Unhandled exception on {Method} {Path}", context.HttpContext.Request.Method, context.HttpContext.Request.Path);

        var message = context.Exception.InnerException != null
            ? $"{context.Exception.Message} → {context.Exception.InnerException.Message}"
            : context.Exception.Message;

        context.Result = new OkObjectResult(new SquidResponse
        {
            Code = statusCode,
            Msg = message
        });

        context.ExceptionHandled = true;
    }
}
