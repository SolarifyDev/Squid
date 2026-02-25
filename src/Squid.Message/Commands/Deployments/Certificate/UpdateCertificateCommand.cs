using Squid.Message.Models.Deployments.Certificate;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Certificate;

public class UpdateCertificateCommand : ICommand
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string Notes { get; set; }
    public List<int> EnvironmentIds { get; set; }
}

public class UpdateCertificateResponse : SquidResponse<UpdateCertificateResponseData>
{
}

public class UpdateCertificateResponseData
{
    public CertificateDto Certificate { get; set; }
}
