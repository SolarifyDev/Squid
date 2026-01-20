namespace Squid.Message.Requests;

public interface ICachingRequest : IRequest
{
    string GetCacheKey();
    
    TimeSpan? GetCacheExpiration();
}