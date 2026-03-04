using Squid.Message.Commands.Deployments.Certificate;

namespace Squid.Message.Events.Deployments.Certificate;

public class CertificateDeletedEvent : IEvent
{
    public DeleteCertificatesResponseData Data { get; set; }
}
