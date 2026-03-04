using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Certificate;

public class DeleteCertificatesCommand : ICommand
{
    public List<int> Ids { get; set; }
}

public class DeleteCertificatesResponse : SquidResponse<DeleteCertificatesResponseData>
{
}

public class DeleteCertificatesResponseData
{
    public List<int> FailIds { get; set; }
}
