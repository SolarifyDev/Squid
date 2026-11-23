using System.Text.Json;
using Mediator.Net;
using ModelContextProtocol.Protocol;

namespace Squid.Mcp.Tools;

internal delegate ValueTask<CallToolResult> ReadToolHandler(
    ReadToolRegistration tool,
    IReadOnlyDictionary<string, JsonElement> arguments,
    IMediator mediator,
    CancellationToken cancellationToken,
    ToolInvocationAudit audit);

internal sealed class ReadToolRegistration
{
    private readonly ReadToolHandler _handler;

    internal ReadToolRegistration(Tool definition, ReadToolSort sort, ReadToolHandler handler)
    {
        Definition = definition;
        Sort = sort;
        _handler = handler;
    }

    internal string Name => Definition.Name;

    internal Tool Definition { get; }

    internal ReadToolSort Sort { get; }

    internal ValueTask<CallToolResult> InvokeAsync(
        IReadOnlyDictionary<string, JsonElement> arguments,
        IMediator mediator,
        CancellationToken cancellationToken,
        ToolInvocationAudit audit)
    {
        return _handler(this, arguments, mediator, cancellationToken, audit);
    }
}

internal enum ReadToolSort
{
    None,
    Id,
    CreatedAt
}
