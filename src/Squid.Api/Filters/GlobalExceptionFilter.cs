using System.Net;
using FluentValidation;
using Microsoft.AspNetCore.Mvc.Filters;
using Serilog;
using Squid.Core.Services.Authorization;
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
            MachineEndpointUpdateNotApplicableException => HttpStatusCode.BadRequest,
            _ => HttpStatusCode.InternalServerError
        };

        Log.Error(context.Exception, "Unhandled exception on {Method} {Path}", context.HttpContext.Request.Method, context.HttpContext.Request.Path);

        var message = context.Exception.InnerException != null
            ? $"{context.Exception.Message} → {context.Exception.InnerException.Message}"
            : context.Exception.Message;

        var response = new SquidResponse
        {
            Code = statusCode,
            Msg = message
        };

        // PR 2 (1.7.0): when the failure is a permission denial, enrich the
        // response with the missing permission name + the built-in roles that
        // grant it. Tentacle CLI and install-script consumers read these
        // structured fields and emit operator-actionable hints — without this,
        // every 403 is a bare "permission denied" that operators have to debug
        // by reading server source.
        if (context.Exception is PermissionDeniedException permissionEx)
        {
            EnrichForPermissionDenial(response, permissionEx);
        }

        context.Result = new OkObjectResult(response);
        context.ExceptionHandled = true;
    }

    private static void EnrichForPermissionDenial(SquidResponse response, PermissionDeniedException ex)
    {
        var permissionName = ex.Permission.ToString();
        var suggestedRoles = PermissionRoleResolver.GetBuiltInRolesGranting(ex.Permission);
        var hint = PermissionRoleResolver.BuildOperatorHint(ex.Permission);

        response.MissingPermission = permissionName;
        response.SuggestedRoles = suggestedRoles;
        response.Msg = $"{response.Msg} {hint}";
    }
}
