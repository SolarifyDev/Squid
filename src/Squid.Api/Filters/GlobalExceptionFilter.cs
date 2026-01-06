using System.Net;
using FluentValidation;
using Microsoft.AspNetCore.Mvc.Filters;
using Squid.Message.Response;

namespace Squid.Api.Filters;

public class GlobalExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        var statusCode = context.Exception switch
        {
            ValidationException => HttpStatusCode.BadRequest,
            _ => HttpStatusCode.InternalServerError
        };

        context.Result = new OkObjectResult(new SquidResponse
        {
            Code = statusCode,
            Msg = context.Exception.Message
        });

        context.ExceptionHandled = true;
    }
}