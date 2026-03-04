using Squid.Message.Enums;

namespace Squid.Message.Models.Deployments.Certificate;

public class CertificateDto : IBaseModel
{
    public int Id { get; set; }
    public int SpaceId { get; set; }
    public string Name { get; set; }
    public string Notes { get; set; }
    public CertificateDataFormat CertificateDataFormat { get; set; }
    public bool PasswordHasValue { get; set; }

    // X.509 metadata
    public string Thumbprint { get; set; }
    public string SubjectDistinguishedName { get; set; }
    public string SubjectCommonName { get; set; }
    public string IssuerDistinguishedName { get; set; }
    public string IssuerCommonName { get; set; }
    public bool SelfSigned { get; set; }
    public DateTimeOffset NotAfter { get; set; }
    public DateTimeOffset NotBefore { get; set; }
    public bool IsExpired { get; set; }
    public bool HasPrivateKey { get; set; }
    public int Version { get; set; }
    public string SerialNumber { get; set; }
    public string SignatureAlgorithmName { get; set; }
    public List<string> SubjectAlternativeNames { get; set; }

    // Scoping
    public List<int> EnvironmentIds { get; set; }

    // Lifecycle
    public DateTimeOffset? Archived { get; set; }
    public int? ReplacedBy { get; set; }

    // Audit
    public DateTimeOffset? LastModifiedOn { get; set; }
    public string LastModifiedBy { get; set; }
}
