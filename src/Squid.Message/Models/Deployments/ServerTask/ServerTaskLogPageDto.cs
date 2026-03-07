namespace Squid.Message.Models.Deployments.ServerTask;

public class ServerTaskLogPageDto
{
    public List<ServerTaskLogElementDto> Items { get; set; } = [];

    public long? NextAfterSequenceNumber { get; set; }

    public long? LastSequenceNumber { get; set; }

    public bool HasMore { get; set; }
}
