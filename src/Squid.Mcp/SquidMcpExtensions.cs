using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Squid.Mcp.Tools;

namespace Squid.Mcp;

public static class SquidMcpExtensions
{
    public static IServiceCollection AddSquidMcp(this IServiceCollection services)
    {
        services.AddLogging(logging =>
            logging.AddFilter("ModelContextProtocol", LogLevel.Warning));

        services.AddMcpServer()
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithMessageFilters(filters => filters.AddOutgoingFilter(next =>
                async (context, cancellationToken) =>
                {
                    if (context.JsonRpcMessage is JsonRpcError)
                        SquidMcpAudit.RecordProtocolError(context.Services);

                    await next(context, cancellationToken).ConfigureAwait(false);
                }))
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools = ReadToolCatalog.ToolDefinitions.ToList()
            }))
            .WithCallToolHandler((context, cancellationToken) =>
                ReadToolCatalog.CallAsync(context, cancellationToken));

        return services;
    }
}
