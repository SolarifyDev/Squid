using System.Security.Cryptography.X509Certificates;

namespace Squid.Tentacle.Certificate;

public interface ITentacleCertificateManager
{
    X509Certificate2 LoadOrCreateCertificate();

    string LoadOrCreateSubscriptionId();
}
