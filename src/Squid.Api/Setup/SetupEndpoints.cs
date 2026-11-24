using Squid.Core.Setup.Install;
using Squid.Message.Models.Setup;

namespace Squid.Api.Setup;

/// <summary>The <c>/api/setup/*</c> routes served while an install has no database yet.</summary>
public static class SetupEndpoints
{
    public const string RoutePrefix = "/api/setup";

    private const string InvalidTokenMessage = "Invalid or missing setup token. It is printed in the server log and stored in the data directory's setup-token file.";

    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(2);

    public static void MapSetupEndpoints(this IEndpointRouteBuilder endpoints, SetupSession session)
    {
        endpoints.MapGet($"{RoutePrefix}/status", () => Results.Ok(session.BuildStatus()));

        var guarded = endpoints.MapGroup(RoutePrefix).AddEndpointFilter((ctx, next) => RequireToken(session, ctx, next));

        guarded.MapPost("/database/test", (DatabaseConnectionTestRequest request, CancellationToken ct) => TestDatabaseAsync(session, request, ct));
        guarded.MapPost("/redis/test", (RedisConnectionTestRequest request) => RedisConnectionTester.TestAsync(request.ConnectionString));
        guarded.MapPost("/install", (SetupInstallRequest request, IHostApplicationLifetime lifetime) => StartInstall(session, request, lifetime));
        guarded.MapGet("/progress", () => Results.Ok(session.Tracker.Snapshot()));
    }

    private static async ValueTask<object> RequireToken(SetupSession session, EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var supplied = ctx.HttpContext.Request.Headers[SetupToken.HeaderName].ToString();

        if (session.Token.Matches(supplied)) return await next(ctx).ConfigureAwait(false);

        return Results.Json(new { message = InvalidTokenMessage }, statusCode: StatusCodes.Status401Unauthorized);
    }

    private static async Task<DatabaseConnectionTestResult> TestDatabaseAsync(SetupSession session, DatabaseConnectionTestRequest request, CancellationToken ct)
    {
        if (request.Mode == SetupServiceMode.External) return await DatabaseConnectionTester.TestAsync(request.ConnectionString, ct).ConfigureAwait(false);

        if (!session.Capabilities.BuiltinDatabaseAvailable) return new DatabaseConnectionTestResult { Success = false, Message = "Built-in PostgreSQL is not available in this installation" };

        return new DatabaseConnectionTestResult { Success = true, DatabaseExists = false, CanCreateDatabase = true, Message = "Built-in PostgreSQL will be initialised in the data directory" };
    }

    private static IResult StartInstall(SetupSession session, SetupInstallRequest request, IHostApplicationLifetime lifetime)
    {
        try
        {
            SetupRequestValidator.EnsureValid(request, session.Capabilities);
        }
        catch (SetupValidationException ex)
        {
            return Results.BadRequest(new { message = ex.Message, errors = ex.Errors });
        }

        if (!session.Tracker.TryBegin(SetupInstaller.Steps)) return Results.Conflict(new { message = "An install is already running or has completed" });

        _ = Task.Run(() => RunInstallAsync(session, request, lifetime));

        return Results.Accepted(value: new { accepted = true });
    }

    private static async Task RunInstallAsync(SetupSession session, SetupInstallRequest request, IHostApplicationLifetime lifetime)
    {
        try
        {
            await new SetupInstaller(session.Embedded, session.Configuration, session.Tracker).InstallAsync(request, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return;
        }

        session.MarkCompleted();

        // Let the wizard observe "Completed" before the port is handed to the normal API host.
        await Task.Delay(ShutdownGrace).ConfigureAwait(false);

        lifetime.StopApplication();
    }
}
