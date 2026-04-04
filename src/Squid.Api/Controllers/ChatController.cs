using System.Text.Json;
using System.Text.Json.Serialization;
using Squid.Message.Commands.Chat;
using Microsoft.AspNetCore.Authorization;

namespace Squid.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/chat")]
public class ChatController : ControllerBase
{
    private readonly IMediator _mediator;

    private static readonly JsonSerializerOptions StreamJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ChatController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("openclaw")]
    public async Task OpenClawChatAsync([FromBody] SendOpenClawChatCommand command, CancellationToken ct)
    {
        try
        {
            var response = await _mediator.SendAsync<SendOpenClawChatCommand, SendOpenClawChatResponse>(command, ct).ConfigureAwait(false);

            if (response.Data?.StreamEvents != null)
                await WriteStreamResponseAsync(response.Data.StreamEvents, ct).ConfigureAwait(false);
            else
                await Response.WriteAsJsonAsync(response, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected — nothing to write
        }
    }

    private async Task WriteStreamResponseAsync(IAsyncEnumerable<OpenClawChatStreamEvent> streamEvents, CancellationToken ct)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        try
        {
            await foreach (var evt in streamEvents.WithCancellation(ct).ConfigureAwait(false))
            {
                var json = JsonSerializer.Serialize(evt, StreamJsonOptions);
                await Response.WriteAsync($"data: {json}\n\n", ct).ConfigureAwait(false);
                await Response.Body.FlushAsync(ct).ConfigureAwait(false);
            }

            await Response.WriteAsync("data: [DONE]\n\n", ct).ConfigureAwait(false);
            await Response.Body.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected — nothing to write
        }
    }
}
