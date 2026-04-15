using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Serilog;

namespace Squid.Tentacle.Certificate;

public class TentacleCertificateManager : ITentacleCertificateManager
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
            return X509CertificateLoader.LoadPkcs12FromFile(certPath, CertPassword);
        }

        Log.Information("Generating new self-signed tentacle certificate");

        var cert = CreateSelfSignedCert();
        EnsureDirectoryExists(_certsPath);
        File.WriteAllBytes(certPath, cert.Export(X509ContentType.Pfx, CertPassword));

        Log.Information("Tentacle certificate saved to {Path}, thumbprint={Thumbprint}", certPath, cert.Thumbprint);

        return cert;
    }

    public string LoadOrCreateSubscriptionId(string overrideSubscriptionId = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideSubscriptionId))
        {
            Log.Information("Using externally-provided subscription ID: {SubscriptionId}", overrideSubscriptionId);
            return overrideSubscriptionId;
        }

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

    /// <summary>
    /// How long generated Tentacle certificates are valid for.
    ///
    /// Aligned with Octopus Tentacle (100 years) — see
    /// <c>/Users/mars/Projects/octopus/OctopusShared/source/Octopus.Shared/Security/CertificateGenerator.cs</c>.
    /// The reasoning is that we're doing TLS pinning by <b>thumbprint</b>, not CA
    /// chain validation, so the <c>NotAfter</c> field isn't a security boundary
    /// — key quality is. A 100-year validity eliminates the operational burden
    /// of renewal (which would require re-registering every Tentacle with the
    /// Server). If a cert's private key ever leaks, the right response is to
    /// delete it and register a new one, not to wait for expiry.
    /// </summary>
    internal const int CertificateValidityYears = 100;

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
            DateTimeOffset.UtcNow.AddYears(CertificateValidityYears));

        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx, CertPassword),
            CertPassword,
            X509KeyStorageFlags.Exportable);
    }

    private static void EnsureDirectoryExists(string path)
    {
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);
    }
}
