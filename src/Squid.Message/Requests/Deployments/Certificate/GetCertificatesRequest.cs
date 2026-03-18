using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Certificate;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Certificate;

[RequiresPermission(Permission.AccountView)]
public class GetCertificatesRequest : IPaginatedRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public int PageIndex { get; set; } = 1;

    public int PageSize { get; set; } = 20;
}

public class GetCertificatesResponse : SquidResponse<GetCertificatesResponseData>
{
}

public class GetCertificatesResponseData
{
    public int Count { get; set; }

    public List<CertificateDto> Certificates { get; set; }
}
