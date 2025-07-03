using System.Net;

namespace Squid.Core.Response;

public class SquidResponse : IResponse
{
    public string Msg { get; set; }

    public HttpStatusCode Code { get; set; }
}

public class SquidResponse<T> : SquidResponse
{
    public T Data { get; set; }
}