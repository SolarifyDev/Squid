using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Serilog;

namespace Squid.Tentacle.Certificate;

public class TentacleCertificateManager
{
    private const string CertFileName = "tentacle-cert.pfx";
    private const string CertPassword = "squid-tentacle-cert";
    private const string SubscriptionIdFileName = "subscription-id";

    private readonly string _certsPath;

    public TentacleCertificateManager(string certsPath)
    {
        _certsPath = certsPath;
    }

    public X509Certificate2 LoadOrCreateCertificate()
    {
        var certPath = Path.Combine(_certsPath, CertFileName);

        if (File.Exists(certPath))
        {
            Log.Information("Loading existing tentacle certificate from {Path}", certPath);
            return new X509Certificate2(certPath, CertPassword, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
        }

        Log.Information("Generating new self-signed tentacle certificate");

        var cert = CreateSelfSignedCert();
        EnsureDirectoryExists(_certsPath);
        File.WriteAllBytes(certPath, cert.Export(X509ContentType.Pfx, CertPassword));

        Log.Information("Tentacle certificate saved to {Path}, thumbprint={Thumbprint}", certPath, cert.Thumbprint);

        return cert;
    }

    public string LoadOrCreateSubscriptionId()
    {
        var idPath = Path.Combine(_certsPath, SubscriptionIdFileName);

        if (File.Exists(idPath))
        {
            var existingId = File.ReadAllText(idPath).Trim();

            if (!string.IsNullOrEmpty(existingId))
            {
                Log.Information("Loaded existing subscription ID: {SubscriptionId}", existingId);
                return existingId;
            }
        }

        var newId = Guid.NewGuid().ToString("N");

        EnsureDirectoryExists(_certsPath);
        File.WriteAllText(idPath, newId);

        Log.Information("Generated new subscription ID: {SubscriptionId}", newId);

        return newId;
    }

    private static X509Certificate2 CreateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);

        var request = new CertificateRequest(
            "CN=squid-tentacle",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        using var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddYears(5));

#pragma warning disable SYSLIB0057
        return new X509Certificate2(
            cert.Export(X509ContentType.Pfx, CertPassword),
            CertPassword,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
#pragma warning restore SYSLIB0057
    }

    private static void EnsureDirectoryExists(string path)
    {
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);
    }
}
