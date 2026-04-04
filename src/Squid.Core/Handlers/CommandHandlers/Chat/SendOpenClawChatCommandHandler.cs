using Squid.Core.Services.Chat;
using Squid.Message.Commands.Chat;

namespace Squid.Core.Handlers.CommandHandlers.Chat;

public class SendOpenClawChatCommandHandler(IOpenClawChatService chatService) : ICommandHandler<SendOpenClawChatCommand, SendOpenClawChatResponse>
{
    public async Task<SendOpenClawChatResponse> Handle(IReceiveContext<SendOpenClawChatCommand> context, CancellationToken cancellationToken)
    {
        var command = context.Message;

        if (command.Stream)
        {
            return new SendOpenClawChatResponse
            {
                Data = new SendOpenClawChatResponseData { StreamEvents = chatService.StreamAsync(command, cancellationToken) }
            };
        }

        var results = await chatService.SendAsync(command, cancellationToken).ConfigureAwait(false);

        return new SendOpenClawChatResponse { Data = new SendOpenClawChatResponseData { Results = results } };
    }
}
