using Squid.Message.Commands.Deployments.Environment;

namespace Squid.Message.Events.Deployments.Environment;

public class EnvironmentDeletedEvent : IEvent
{
    public DeleteEnvironmentsResponseData Data { get; set; }
}
