using Squid.Message.Models.Deployments.Certificate;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Certificate;

public class ReplaceCertificateCommand : ICommand
{
    public int Id { get; set; }
    public string CertificateData { get; set; }
    public string Password { get; set; }
}

public class ReplaceCertificateResponse : SquidResponse<ReplaceCertificateResponseData>
{
}

public class ReplaceCertificateResponseData
{
    public CertificateDto Certificate { get; set; }
}
