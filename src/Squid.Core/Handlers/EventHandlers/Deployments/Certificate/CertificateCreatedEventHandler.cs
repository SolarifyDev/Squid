using Squid.Message.Events.Deployments.Certificate;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Certificate
{
    public class CertificateCreatedEventHandler : IEventHandler<CertificateCreatedEvent>
    {
        public Task Handle(IReceiveContext<CertificateCreatedEvent> context, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
