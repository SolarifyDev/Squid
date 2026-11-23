using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Serilog;
using Serilog.Events;
using Serilog.Filters;
using Squid.Mcp;
using Squid.Message.Constants;

namespace Squid.Api.Extensions;

public static class McpEndpointExtensions
{
    public const string EndpointPath = "/mcp";
    public const string ApiKeyPolicyName = "McpApiKey";
    public const string CorsPolicyName = "McpApiKey";

    private static readonly string[] AllowedHeaders =
    [
        "Accept",
        "Content-Type",
        "MCP-Protocol-Version",
        "Mcp-Method",
        "Mcp-Name",
        "X-API-KEY"
    ];

    public static LoggerConfiguration ConfigureMcpLogging(this LoggerConfiguration loggerConfiguration)
    {
        return loggerConfiguration
            .MinimumLevel.Override("ModelContextProtocol", LogEventLevel.Warning)
            .Filter.ByExcluding(Matching.FromSource("ModelContextProtocol"));
    }

    public static IServiceCollection AddSquidMcpEndpoint(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (!IsEnabled(configuration))
        {
            services.AddSingleton<IStartupFilter, McpDisabledStartupFilter>();
            return services;
        }

        services.AddSquidMcp();
        services.AddAuthorization(options =>
        {
            options.AddPolicy(ApiKeyPolicyName, policy =>
            {
                policy
                    .AddAuthenticationSchemes(AuthenticationSchemeConstants.ApiKeyAuthenticationScheme)
                    .RequireAuthenticatedUser();
            });
        });
        services.AddMcpCors(configuration);
        services.AddSingleton<IStartupFilter, McpEndpointStartupFilter>();

        return services;
    }

    public static IEndpointRouteBuilder MapSquidMcpEndpoint(
        this IEndpointRouteBuilder endpoints,
        IConfiguration configuration)
    {
        if (!IsEnabled(configuration)) return endpoints;

        endpoints.MapMcp(EndpointPath)
            .WithMetadata(new McpSdkEndpointMetadata())
            .RequireAuthorization(ApiKeyPolicyName)
            .RequireCors(CorsPolicyName);

        endpoints.MapGet(EndpointPath, WriteStatelessGetNotAllowedAsync)
            .RequireAuthorization(ApiKeyPolicyName)
            .RequireCors(CorsPolicyName);

        return endpoints;
    }

    private static Task WriteStatelessGetNotAllowedAsync(HttpContext context)
    {
        context.Response.Headers.Allow = HttpMethods.Post;
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        return Task.CompletedTask;
    }

    private static void AddMcpCors(this IServiceCollection services, IConfiguration configuration)
    {
        var origins = GetAllowedOrigins(configuration);

        services.AddCors(options =>
        {
            options.AddPolicy(CorsPolicyName, policy =>
            {
                policy
                    .WithMethods(HttpMethods.Post)
                    .WithHeaders(AllowedHeaders);

                if (origins.Length == 0)
                    policy.SetIsOriginAllowed(_ => false);
                else
                    policy.WithOrigins(origins);
            });
        });
    }

    internal static bool IsEnabled(IConfiguration configuration)
    {
        return bool.TryParse(configuration["Mcp:Enabled"], out var enabled) && enabled;
    }

    internal static string[] GetAllowedOrigins(IConfiguration configuration)
    {
        return configuration
            .GetSection("Mcp:AllowedOrigins")
            .GetChildren()
            .Select(child => child.Value?.Trim())
            .Where(origin => !string.IsNullOrWhiteSpace(origin))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

internal sealed class McpSdkEndpointMetadata;
