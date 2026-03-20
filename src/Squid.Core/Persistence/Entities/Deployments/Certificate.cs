using Squid.Message.Enums;

namespace Squid.Core.Persistence.Entities.Deployments;

public class Certificate : IEntity<int>, IAuditable
{
    public int Id { get; set; }
    public int SpaceId { get; set; }
    public string Name { get; set; }
    public string Notes { get; set; }

    // Sensitive data (encrypted at rest)
    public string CertificateData { get; set; }
    public string Password { get; set; }
    public CertificateDataFormat CertificateDataFormat { get; set; }
    public bool PasswordHasValue => !string.IsNullOrWhiteSpace(Password);

    // X.509 parsed metadata
    public string Thumbprint { get; set; }
    public string SubjectDistinguishedName { get; set; }
    public string SubjectCommonName { get; set; }
    public string IssuerDistinguishedName { get; set; }
    public string IssuerCommonName { get; set; }
    public bool SelfSigned { get; set; }
    public DateTimeOffset NotAfter { get; set; }
    public DateTimeOffset NotBefore { get; set; }
    public bool IsExpired => DateTimeOffset.UtcNow > NotAfter;
    public bool HasPrivateKey { get; set; }
    public int Version { get; set; }
    public string SerialNumber { get; set; }
    public string SignatureAlgorithmName { get; set; }
    public string SubjectAlternativeNames { get; set; }

    // Scoping
    public string EnvironmentIds { get; set; }

    // Lifecycle
    public DateTimeOffset? Archived { get; set; }
    public int? ReplacedBy { get; set; }

    // IAuditable
    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
