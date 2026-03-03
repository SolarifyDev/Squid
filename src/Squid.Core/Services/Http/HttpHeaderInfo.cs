using System.Net;
using Microsoft.AspNetCore.Http;

namespace Squid.Core.Services.Http;

public interface IHttpHeaderInfo : IScopedDependency
{
    public string AskBy { get; set; }
}

public class HttpHeaderInfo : IHttpHeaderInfo
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpHeaderInfo(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private string _askBy;

    public string AskBy
    {
        get
        {
            if (string.IsNullOrEmpty(_askBy))
            {
                _askBy = WebUtility.UrlDecode(GetHeaderValue<string>(nameof(AskBy)));
            }
            return _askBy;
        }
        set => _askBy = value;
    }

    private T GetHeaderValue<T>(string key)
    {
        var headerValue = _httpContextAccessor?.HttpContext?.Request.Headers
            .SingleOrDefault(x => x.Key.Equals(key, StringComparison.InvariantCultureIgnoreCase)).Value;

        if (string.IsNullOrEmpty(headerValue))
            return default;
        
        return (T)Convert.ChangeType(headerValue.ToString(), typeof(T));
    }
}