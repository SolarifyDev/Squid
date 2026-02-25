using Squid.Message.Models.Deployments.Certificate;

namespace Squid.Message.Events.Deployments.Certificate;

public class CertificateUpdatedEvent : IEvent
{
    public CertificateDto Data { get; set; }
}
