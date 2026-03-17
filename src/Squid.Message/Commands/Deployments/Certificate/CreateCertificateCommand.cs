using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Certificate;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Certificate;

[RequiresPermission(Permission.AccountCreate)]
public class CreateCertificateCommand : ICommand
{
    public string Name { get; set; }
    public string Notes { get; set; }
    public List<int> EnvironmentIds { get; set; }
    public int SpaceId { get; set; }

    // Import mode — provide CertificateData (base64 PFX/PEM/DER)
    public string CertificateData { get; set; }
    public string Password { get; set; }

    // Generate mode — when CertificateData is null, generate self-signed
    public string CommonName { get; set; }
    public CertificateKeyType KeyType { get; set; } = CertificateKeyType.RSA2048;
    public int ValidityDays { get; set; } = 365;
    public List<string> SubjectAlternativeNames { get; set; }
}

public class CreateCertificateResponse : SquidResponse<CreateCertificateResponseData>
{
}

public class CreateCertificateResponseData
{
    public CertificateDto Certificate { get; set; }
}
