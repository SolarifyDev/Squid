using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Serilog;
using Squid.Tentacle.Security;

namespace Squid.Tentacle.Certificate;

public class TentacleCertificateManager : ITentacleCertificateManager
{
    private const string CertFileName = "tentacle-cert.pfx";
    private const string LegacyCertPassword = "squid-tentacle-cert";
    private const string SubscriptionIdFileName = "subscription-id";

    // Migration marker — written after encrypting an at-rest secret. Presence
    // tells the loader this machine's secrets are in the encrypted format.
    // Per-file .encrypted markers rather than a global one so a half-migration
    // (e.g. cert encrypted but subscription id failed) is recoverable.
    private const string EncryptedMarkerSuffix = ".encrypted";

    private readonly string _certsPath;
    private readonly IMachineKeyEncryptor? _encryptor;

    public TentacleCertificateManager(string certsPath)
        : this(certsPath, encryptor: null) { }

    public TentacleCertificateManager(string certsPath, IMachineKeyEncryptor? encryptor)
    {
        _certsPath = certsPath;
        _encryptor = encryptor;
    }

    public X509Certificate2 LoadOrCreateCertificate()
    {
        var certPath = Path.Combine(_certsPath, CertFileName);
        var passwordFilePath = certPath + ".pwd";

        var existing = TryLoadExistingCertificate(certPath, passwordFilePath);
        if (existing != null) return existing;

        return GenerateAndPersistCertificate(certPath, passwordFilePath);
    }

    private X509Certificate2 TryLoadExistingCertificate(string certPath, string passwordFilePath)
    {
        if (!File.Exists(certPath)) return null;

        var password = ResolveCertPassword(passwordFilePath);

        try
        {
            Log.Information("Loading existing tentacle certificate from {Path}", certPath);
            return X509CertificateLoader.LoadPkcs12FromFile(certPath, password);
        }
        catch (CryptographicException ex)
        {
            // The stored password no longer opens the PFX. Under an encryptor this almost
            // always means the host machine-id changed (VM clone, container rebuild,
            // /etc/machine-id reset), so the random PFX password can no longer be derived —
            // the cert is unrecoverable. Regenerate a fresh identity rather than crash the
            // agent on boot: it comes up with a NEW thumbprint and MUST be re-registered
            // (the same recovery the subscription id takes on the matching failure).
            Log.Warning(ex, "Existing tentacle certificate at {Path} could not be opened with the stored password — the host machine-id has most likely changed. Regenerating a fresh certificate; the agent will have a NEW thumbprint and MUST be re-registered with the server.", certPath);
            return null;
        }
    }

    private X509Certificate2 GenerateAndPersistCertificate(string certPath, string passwordFilePath)
    {
        Log.Information("Generating new self-signed tentacle certificate");

        // Without an encryptor: keep legacy password so back-compat tests and
        // existing installs that expect the hardcoded password work unchanged.
        // With an encryptor: random per-cert password, stored encrypted.
        var newPassword = _encryptor != null ? GenerateCertPassword() : LegacyCertPassword;
        var cert = CreateSelfSignedCert(newPassword);

        EnsureDirectoryExists(_certsPath);
        File.WriteAllBytes(certPath, cert.Export(X509ContentType.Pfx, newPassword));

        if (_encryptor != null)
            WriteCertPassword(passwordFilePath, newPassword);

        Log.Information("Tentacle certificate saved to {Path}, thumbprint={Thumbprint}", certPath, cert.Thumbprint);

        return cert;
    }

    private string ResolveCertPassword(string passwordFilePath)
    {
        if (_encryptor == null) return LegacyCertPassword;

        if (File.Exists(passwordFilePath))
        {
            try
            {
                var encrypted = File.ReadAllText(passwordFilePath).Trim();
                return _encryptor.Unprotect(encrypted);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to decrypt tentacle cert password at {Path}; falling back to legacy default", passwordFilePath);
                return LegacyCertPassword;
            }
        }

        // First startup under an encryptor-aware build: legacy installation has
        // no password file, the PFX was written with LegacyCertPassword. Migrate
        // by writing the legacy password in encrypted form so subsequent starts
        // use the encryptor-protected path. The PFX itself can be re-encrypted
        // with a rotated password at the next LoadOrCreateCertificate where
        // we regenerate — safer than reopening+resaving the PFX here.
        try
        {
            WriteCertPassword(passwordFilePath, LegacyCertPassword);
            Log.Information("Migrated tentacle cert password storage to encrypted form at {Path}", passwordFilePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to write migrated cert password; continuing with plaintext legacy default");
        }
        return LegacyCertPassword;
    }

    private void WriteCertPassword(string path, string password)
    {
        if (_encryptor == null)
        {
            File.WriteAllText(path, password);
            return;
        }
        File.WriteAllText(path, _encryptor.Protect(password));
    }

    private static string GenerateCertPassword()
    {
        // 32 random bytes → 43-char base64. Never stored plaintext when encryptor
        // is wired; legacy path falls back to the fixed LegacyCertPassword.
        var buffer = new byte[32];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToBase64String(buffer);
    }

    public string LoadOrCreateSubscriptionId(string overrideSubscriptionId = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideSubscriptionId))
        {
            Log.Information("Using externally-provided subscription ID: {SubscriptionId}", overrideSubscriptionId);
            return overrideSubscriptionId;
        }

        var idPath = Path.Combine(_certsPath, SubscriptionIdFileName);
        var encryptedMarker = idPath + EncryptedMarkerSuffix;

        if (File.Exists(idPath))
        {
            var raw = File.ReadAllText(idPath).Trim();

            if (!string.IsNullOrEmpty(raw))
            {
                var existingId = _encryptor != null && File.Exists(encryptedMarker)
                    ? TryDecryptSubscriptionId(raw, idPath)
                    : MigrateToEncryptedIfNeeded(raw, idPath, encryptedMarker);

                if (!string.IsNullOrEmpty(existingId))
                {
                    Log.Information("Loaded existing subscription ID: {SubscriptionId}", existingId);
                    return existingId;
                }

                // existingId == null: the stored id was encrypted but no longer decrypts
                // (host machine-id changed). Fall through to mint a fresh identity, matching
                // the certificate regeneration above — the operator must re-register.
            }
        }

        var newId = Guid.NewGuid().ToString("N");
        EnsureDirectoryExists(_certsPath);
        WriteSubscriptionId(newId, idPath, encryptedMarker);

        Log.Information("Generated new subscription ID: {SubscriptionId}", newId);

        return newId;
    }

    private string TryDecryptSubscriptionId(string raw, string idPath)
    {
        try { return _encryptor!.Unprotect(raw); }
        catch (Exception ex)
        {
            // The stored id carries the encrypted envelope but no longer decrypts — the host
            // machine-id has most likely changed. Returning the raw ciphertext as the
            // subscription id would corrupt the agent's polling identity, so signal
            // "unrecoverable" (null) and let the caller mint a fresh id + re-register.
            Log.Warning(ex, "Subscription id at {Path} could not be decrypted — the host machine-id has most likely changed. Regenerating a fresh subscription id; the agent MUST be re-registered with the server.", idPath);
            return null;
        }
    }

    private string MigrateToEncryptedIfNeeded(string raw, string idPath, string encryptedMarker)
    {
        if (_encryptor == null) return raw;

        try
        {
            var protectedText = _encryptor.Protect(raw);
            File.WriteAllText(idPath, protectedText);
            File.WriteAllText(encryptedMarker, "v1");
            Log.Information("Migrated subscription id {Path} to encrypted-at-rest form", idPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to migrate subscription id at {Path} to encrypted form; continuing plaintext", idPath);
        }
        return raw;
    }

    private void WriteSubscriptionId(string id, string idPath, string encryptedMarker)
    {
        if (_encryptor == null)
        {
            File.WriteAllText(idPath, id);
            return;
        }
        File.WriteAllText(idPath, _encryptor.Protect(id));
        File.WriteAllText(encryptedMarker, "v1");
    }

    /// <summary>
    /// How long generated Tentacle certificates are valid for.
    ///
    /// Aligned with Octopus Tentacle (100 years) — see
    /// <c>OctopusTentacle/source/Octopus.Tentacle/Certificates/CertificateGenerator.cs</c>
    /// on the public GitHub repo. The reasoning is that we're doing TLS pinning
    /// by <b>thumbprint</b>, not CA chain validation, so the <c>NotAfter</c>
    /// field isn't a security boundary — key quality is. A 100-year validity
    /// eliminates the operational burden of renewal (which would require
    /// re-registering every Tentacle with the Server). If a cert's private key
    /// ever leaks, the right response is to delete it and register a new one,
    /// not to wait for expiry.
    /// </summary>
    internal const int CertificateValidityYears = 100;

    /// <summary>
    /// How far back to set <c>NotBefore</c> from "now". Also aligned with
    /// Octopus — absorbs clock skew between this machine and whoever receives
    /// the cert (Squid Server validating TLS, another Tentacle peer, etc.).
    /// Without this buffer, a receiver whose clock is even seconds ahead of
    /// the generator would see <c>CertNotYetValid</c> for the freshly minted
    /// cert. One day covers NTP drift, DST boundaries, and timezone mistakes.
    /// </summary>
    internal static readonly TimeSpan NotBeforeClockSkewBuffer = TimeSpan.FromDays(1);

    private static X509Certificate2 CreateSelfSignedCert(string password)
    {
        using var rsa = RSA.Create(2048);

        var request = new CertificateRequest(
            "CN=squid-tentacle",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var now = DateTimeOffset.UtcNow;

        using var cert = request.CreateSelfSigned(
            now - NotBeforeClockSkewBuffer,
            now.AddYears(CertificateValidityYears));

        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx, password),
            password,
            X509KeyStorageFlags.Exportable);
    }

    private static void EnsureDirectoryExists(string path)
    {
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);
    }
}
