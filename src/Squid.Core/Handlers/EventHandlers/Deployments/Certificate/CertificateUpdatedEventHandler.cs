using Squid.Message.Events.Deployments.Certificate;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Certificate
{
    public class CertificateUpdatedEventHandler : IEventHandler<CertificateUpdatedEvent>
    {
        public Task Handle(IReceiveContext<CertificateUpdatedEvent> context, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
