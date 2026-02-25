using Squid.Message.Models.Deployments.Certificate;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Certificate;

public class CreateCertificateCommand : ICommand
{
    public string Name { get; set; }
    public string Notes { get; set; }
    public string CertificateData { get; set; }
    public string Password { get; set; }
    public List<int> EnvironmentIds { get; set; }
    public int SpaceId { get; set; }
}

public class CreateCertificateResponse : SquidResponse<CreateCertificateResponseData>
{
}

public class CreateCertificateResponseData
{
    public CertificateDto Certificate { get; set; }
}
