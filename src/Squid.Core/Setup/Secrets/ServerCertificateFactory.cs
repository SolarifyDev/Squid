using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Squid.Core.Setup.Secrets;

/// <summary>
/// Creates the per-install Halibut / HTTPS server identity: a self-signed RSA certificate whose
/// thumbprint Tentacles pin, exported as password-protected PKCS#12 in the same shape the
/// <c>SelfCert</c> settings expect.
/// </summary>
public static class ServerCertificateFactory
{
    public const string SubjectName = "CN=squid-server";

    private const int KeySizeBits = 2048;
    private const int ValidityYears = 100;

    public static GeneratedServerCertificate Create()
    {
        using var rsa = RSA.Create(KeySizeBits);

        var request = new CertificateRequest(SubjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);

        using var certificate = request.CreateSelfSigned(notBefore, notBefore.AddYears(ValidityYears));

        var password = RandomSecret.Base64(24);
        var pfx = certificate.Export(X509ContentType.Pkcs12, password);

        return new GeneratedServerCertificate(Convert.ToBase64String(pfx), password, certificate.Thumbprint);
    }
}

public sealed record GeneratedServerCertificate(string Base64, string Password, string Thumbprint);
