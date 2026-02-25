using Squid.Message.Events.Deployments.Certificate;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Certificate
{
    public class CertificateReplacedEventHandler : IEventHandler<CertificateReplacedEvent>
    {
        public Task Handle(IReceiveContext<CertificateReplacedEvent> context, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
