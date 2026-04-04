using System.Text.Json.Serialization;
using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Chat;

[RequiresPermission(Permission.MachineEdit)]
public class SendOpenClawChatCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public List<string> TargetTags { get; set; }

    // OpenAI-compatible params
    public List<ChatMessageDto> Messages { get; set; }
    public string Model { get; set; }
    public bool Stream { get; set; }
    public double? Temperature { get; set; }
    public int? MaxTokens { get; set; }
    public string ResponseFormat { get; set; }
    public string User { get; set; }

    // OpenClaw-specific
    public string ModelOverride { get; set; }
    public string AgentId { get; set; }
    public string Channel { get; set; }
    public string SessionKey { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
}

public class ChatMessageDto
{
    public string Role { get; set; }
    public string Content { get; set; }
}

public class SendOpenClawChatResponse : SquidResponse<SendOpenClawChatResponseData>
{
}

public class SendOpenClawChatResponseData
{
    public List<OpenClawChatResultItem> Results { get; set; } = new();
    [JsonIgnore]
    public IAsyncEnumerable<OpenClawChatStreamEvent> StreamEvents { get; set; }
}

public class OpenClawChatResultItem
{
    public int MachineId { get; set; }
    public string MachineName { get; set; }
    public bool Succeeded { get; set; }
    public string Content { get; set; }
    public string Model { get; set; }
    public string FinishReason { get; set; }
    public string Error { get; set; }
}

public class OpenClawChatStreamEvent
{
    public int MachineId { get; set; }
    public string MachineName { get; set; }
    public string Delta { get; set; }
    public string Model { get; set; }
    public string FinishReason { get; set; }
    public string Error { get; set; }
}
