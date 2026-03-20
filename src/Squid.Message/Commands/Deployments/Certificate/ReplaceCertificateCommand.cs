using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Certificate;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Certificate;

[RequiresPermission(Permission.AccountEdit)]
public class ReplaceCertificateCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }
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
