using System.Diagnostics;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Squid.Message.Constants;

namespace Squid.Api.Extensions;

internal sealed class McpDisabledStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Path.StartsWithSegments(McpEndpointExtensions.EndpointPath))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                await nextMiddleware().ConfigureAwait(false);
            });

            next(app);
        };
    }
}

internal sealed class McpEndpointStartupFilter : IStartupFilter
{
    private readonly HashSet<string> _allowedOrigins;
    private readonly ILogger<McpEndpointStartupFilter> _logger;

    public McpEndpointStartupFilter(
        IConfiguration configuration,
        ILogger<McpEndpointStartupFilter> logger)
    {
        _allowedOrigins = new HashSet<string>(
            McpEndpointExtensions.GetAllowedOrigins(configuration),
            StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            ValidateApiKeyScheme(app.ApplicationServices);

            app.Use(async (context, nextMiddleware) =>
            {
                if (!context.Request.Path.StartsWithSegments(McpEndpointExtensions.EndpointPath))
                {
                    await nextMiddleware().ConfigureAwait(false);
                    return;
                }

                var started = Stopwatch.GetTimestamp();

                try
                {
                    if (!IsOriginAllowed(context.Request))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return;
                    }

                    await nextMiddleware().ConfigureAwait(false);
                }
                finally
                {
                    McpRequestAudit.LogRequest(_logger, context, started);
                }
            });

            next(app);
        };
    }

    private void ValidateApiKeyScheme(IServiceProvider services)
    {
        var provider = services.GetService<IAuthenticationSchemeProvider>();
        var apiKeyScheme = provider
            ?.GetSchemeAsync(AuthenticationSchemeConstants.ApiKeyAuthenticationScheme)
            .GetAwaiter()
            .GetResult();

        if (apiKeyScheme == null)
        {
            throw new InvalidOperationException(
                "Mcp:Enabled requires the API Key authentication scheme to be registered.");
        }
    }

    private bool IsOriginAllowed(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(HeaderNames.Origin, out var origins)) return true;
        if (origins.Count != 1) return false;

        var origin = origins[0];

        return !string.IsNullOrWhiteSpace(origin) && _allowedOrigins.Contains(origin);
    }
}
