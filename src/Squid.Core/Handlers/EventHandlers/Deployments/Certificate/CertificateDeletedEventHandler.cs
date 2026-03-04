using Squid.Message.Events.Deployments.Certificate;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Certificate
{
    public class CertificateDeletedEventHandler : IEventHandler<CertificateDeletedEvent>
    {
        public Task Handle(IReceiveContext<CertificateDeletedEvent> context, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
