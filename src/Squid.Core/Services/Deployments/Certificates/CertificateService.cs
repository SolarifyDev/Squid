using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Squid.Message.Commands.Deployments.Certificate;
using Squid.Message.Enums;
using Squid.Message.Events.Deployments.Certificate;
using Squid.Message.Models.Deployments.Certificate;
using Squid.Message.Requests.Deployments.Certificate;

namespace Squid.Core.Services.Deployments.Certificates;

public interface ICertificateService : IScopedDependency
{
    Task<CertificateCreatedEvent> CreateCertificateAsync(CreateCertificateCommand command, CancellationToken cancellationToken);

    Task<CertificateUpdatedEvent> UpdateCertificateAsync(UpdateCertificateCommand command, CancellationToken cancellationToken);

    Task<CertificateReplacedEvent> ReplaceCertificateAsync(ReplaceCertificateCommand command, CancellationToken cancellationToken);

    Task<CertificateDeletedEvent> DeleteCertificatesAsync(DeleteCertificatesCommand command, CancellationToken cancellationToken);

    Task<GetCertificatesResponse> GetCertificatesAsync(GetCertificatesRequest request, CancellationToken cancellationToken);
}

public class CertificateService(IMapper mapper, ICertificateDataProvider certificateDataProvider)
    : ICertificateService
{
    public async Task<CertificateCreatedEvent> CreateCertificateAsync(CreateCertificateCommand command, CancellationToken cancellationToken)
    {
        var entity = string.IsNullOrEmpty(command.CertificateData)
            ? GenerateSelfSignedEntity(command)
            : ImportCertificateEntity(command);

        entity.SpaceId = command.SpaceId;
        entity.Notes = command.Notes;
        entity.EnvironmentIds = command.EnvironmentIds != null ? string.Join(',', command.EnvironmentIds) : null;
        entity.LastModifiedOn = DateTimeOffset.UtcNow;

        await certificateDataProvider.AddCertificateAsync(entity, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new CertificateCreatedEvent
        {
            Data = mapper.Map<CertificateDto>(entity)
        };
    }

    private static Persistence.Entities.Deployments.Certificate ImportCertificateEntity(CreateCertificateCommand command)
    {
        var certBytes = Convert.FromBase64String(command.CertificateData);
        var entity = ParseCertificate(certBytes, command.Password);

        entity.Name = command.Name;
        entity.CertificateData = command.CertificateData;
        entity.Password = command.Password;

        return entity;
    }

    private static Persistence.Entities.Deployments.Certificate GenerateSelfSignedEntity(CreateCertificateCommand command)
    {
        using var keyPair = CreateAsymmetricKey(command.KeyType);
        var hashAlgorithm = GetHashAlgorithm(command.KeyType);

        var subject = $"CN={command.Name}";
        var request = CreateCertificateRequest(subject, keyPair, hashAlgorithm);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));

        if (command.SubjectAlternativeNames is { Count: > 0 })
        {
            var sanBuilder = new SubjectAlternativeNameBuilder();

            foreach (var san in command.SubjectAlternativeNames)
                sanBuilder.AddDnsName(san);

            request.CertificateExtensions.Add(sanBuilder.Build());
        }

        var notBefore = DateTimeOffset.UtcNow;
        var notAfter = notBefore.AddDays(command.ValidityDays);

        using var cert = request.CreateSelfSigned(notBefore, notAfter);

        var password = Guid.NewGuid().ToString("N");
        var pfxBytes = cert.Export(X509ContentType.Pfx, password);
        var base64Data = Convert.ToBase64String(pfxBytes);

        var entity = ParseCertificate(pfxBytes, password);

        entity.Name = command.Name;
        entity.CertificateData = base64Data;
        entity.Password = password;

        return entity;
    }

    public async Task<CertificateUpdatedEvent> UpdateCertificateAsync(UpdateCertificateCommand command, CancellationToken cancellationToken)
    {
        var entity = await certificateDataProvider.GetCertificateByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);

        if (entity == null)
            throw new Exception("Certificate not found");

        entity.Name = command.Name;
        entity.Notes = command.Notes;
        entity.EnvironmentIds = command.EnvironmentIds != null ? string.Join(',', command.EnvironmentIds) : null;
        entity.LastModifiedOn = DateTimeOffset.UtcNow;

        await certificateDataProvider.UpdateCertificateAsync(entity, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new CertificateUpdatedEvent
        {
            Data = mapper.Map<CertificateDto>(entity)
        };
    }

    public async Task<CertificateReplacedEvent> ReplaceCertificateAsync(ReplaceCertificateCommand command, CancellationToken cancellationToken)
    {
        var original = await certificateDataProvider.GetCertificateByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);

        if (original == null)
            throw new Exception("Certificate not found");

        var certBytes = Convert.FromBase64String(command.CertificateData);
        var newEntity = ParseCertificate(certBytes, command.Password);

        newEntity.Name = original.Name;
        newEntity.Notes = original.Notes;
        newEntity.CertificateData = command.CertificateData;
        newEntity.Password = command.Password;
        newEntity.SpaceId = original.SpaceId;
        newEntity.EnvironmentIds = original.EnvironmentIds;
        newEntity.LastModifiedOn = DateTimeOffset.UtcNow;

        await certificateDataProvider.AddCertificateAsync(newEntity, cancellationToken: cancellationToken).ConfigureAwait(false);

        original.Archived = DateTimeOffset.UtcNow;
        original.ReplacedBy = newEntity.Id;
        original.LastModifiedOn = DateTimeOffset.UtcNow;

        await certificateDataProvider.UpdateCertificateAsync(original, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new CertificateReplacedEvent
        {
            Data = mapper.Map<CertificateDto>(newEntity)
        };
    }

    public async Task<CertificateDeletedEvent> DeleteCertificatesAsync(DeleteCertificatesCommand command, CancellationToken cancellationToken)
    {
        var certificates = await certificateDataProvider.GetCertificatesByIdsAsync(command.Ids, cancellationToken).ConfigureAwait(false);

        await certificateDataProvider.DeleteCertificatesAsync(certificates, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new CertificateDeletedEvent
        {
            Data = new DeleteCertificatesResponseData
            {
                FailIds = command.Ids.Except(certificates.Select(f => f.Id)).ToList()
            }
        };
    }

    public async Task<GetCertificatesResponse> GetCertificatesAsync(GetCertificatesRequest request, CancellationToken cancellationToken)
    {
        var (count, data) = await certificateDataProvider.GetCertificatePagingAsync(request.PageIndex, request.PageSize, cancellationToken).ConfigureAwait(false);

        return new GetCertificatesResponse
        {
            Data = new GetCertificatesResponseData
            {
                Count = count,
                Certificates = mapper.Map<List<CertificateDto>>(data)
            }
        };
    }

    private static Persistence.Entities.Deployments.Certificate ParseCertificate(byte[] data, string password)
    {
        var (cert, format) = LoadX509Certificate(data, password);

        using (cert)
        {
            var entity = new Persistence.Entities.Deployments.Certificate
            {
                CertificateDataFormat = format,
                Thumbprint = cert.Thumbprint,
                SubjectDistinguishedName = cert.Subject,
                SubjectCommonName = ExtractCommonName(cert.Subject),
                IssuerDistinguishedName = cert.Issuer,
                IssuerCommonName = ExtractCommonName(cert.Issuer),
                SelfSigned = string.Equals(cert.Subject, cert.Issuer, StringComparison.Ordinal),
                NotAfter = new DateTimeOffset(cert.NotAfter.ToUniversalTime(), TimeSpan.Zero),
                NotBefore = new DateTimeOffset(cert.NotBefore.ToUniversalTime(), TimeSpan.Zero),
                HasPrivateKey = cert.HasPrivateKey,
                Version = cert.Version,
                SerialNumber = cert.SerialNumber,
                SignatureAlgorithmName = cert.SignatureAlgorithm.FriendlyName,
                SubjectAlternativeNames = ExtractSubjectAlternativeNames(cert)
            };

            return entity;
        }
    }

    private static (X509Certificate2 cert, CertificateDataFormat format) LoadX509Certificate(byte[] data, string password)
    {
        // Check PEM first (text-based detection is reliable)
        var text = Encoding.UTF8.GetString(data);

        if (text.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            var cert = X509Certificate2.CreateFromPem(text);
            return (cert, CertificateDataFormat.Pem);
        }

        // Try PFX (PKCS#12) — binary format with optional password
        try
        {
            var cert = LoadPfx(data, password);

            if (cert.HasPrivateKey || !string.IsNullOrEmpty(password))
                return (cert, CertificateDataFormat.Pkcs12);

            // Ambiguous: could be DER parsed by PFX constructor — check content type
            var contentType = X509Certificate2.GetCertContentType(data);

            if (contentType == X509ContentType.Pfx)
                return (cert, CertificateDataFormat.Pkcs12);

            cert.Dispose();
        }
        catch (CryptographicException)
        {
        }

        // DER (raw binary certificate)
        var derCert = new X509Certificate2(data);
        return (derCert, CertificateDataFormat.Der);
    }

    private static X509Certificate2 LoadPfx(byte[] data, string password)
    {
        try
        {
            return new X509Certificate2(data, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (PlatformNotSupportedException)
        {
            return new X509Certificate2(data, password);
        }
    }

    private static AsymmetricAlgorithm CreateAsymmetricKey(CertificateKeyType keyType)
    {
        return keyType switch
        {
            CertificateKeyType.RSA2048 => RSA.Create(2048),
            CertificateKeyType.RSA4096 => RSA.Create(4096),
            CertificateKeyType.NistP256 => ECDsa.Create(ECCurve.NamedCurves.nistP256),
            CertificateKeyType.NistP384 => ECDsa.Create(ECCurve.NamedCurves.nistP384),
            CertificateKeyType.NistP521 => ECDsa.Create(ECCurve.NamedCurves.nistP521),
            _ => throw new ArgumentOutOfRangeException(nameof(keyType), keyType, "Unsupported key type")
        };
    }

    private static HashAlgorithmName GetHashAlgorithm(CertificateKeyType keyType)
    {
        return keyType switch
        {
            CertificateKeyType.RSA2048 => HashAlgorithmName.SHA256,
            CertificateKeyType.RSA4096 => HashAlgorithmName.SHA256,
            CertificateKeyType.NistP256 => HashAlgorithmName.SHA256,
            CertificateKeyType.NistP384 => HashAlgorithmName.SHA384,
            CertificateKeyType.NistP521 => HashAlgorithmName.SHA512,
            _ => HashAlgorithmName.SHA256
        };
    }

    private static CertificateRequest CreateCertificateRequest(
        string subject,
        AsymmetricAlgorithm key,
        HashAlgorithmName hashAlgorithm)
    {
        return key switch
        {
            RSA rsa => new CertificateRequest(subject, rsa, hashAlgorithm, RSASignaturePadding.Pkcs1),
            ECDsa ecdsa => new CertificateRequest(subject, ecdsa, hashAlgorithm),
            _ => throw new ArgumentException($"Unsupported key type: {key.GetType().Name}", nameof(key))
        };
    }

    private static string ExtractCommonName(string distinguishedName)
    {
        if (string.IsNullOrEmpty(distinguishedName)) return null;

        foreach (var part in distinguishedName.Split(','))
        {
            var trimmed = part.Trim();

            if (trimmed.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                return trimmed[3..];
        }

        return null;
    }

    private static string ExtractSubjectAlternativeNames(X509Certificate2 cert)
    {
        var sanExtension = cert.Extensions["2.5.29.17"];

        if (sanExtension == null) return null;

        var sanNames = new List<string>();
        var asnData = new AsnEncodedData(sanExtension.Oid, sanExtension.RawData);
        var formatted = asnData.Format(false);

        if (!string.IsNullOrEmpty(formatted))
            sanNames.Add(formatted);

        return sanNames.Count > 0 ? string.Join(',', sanNames) : null;
    }
}
