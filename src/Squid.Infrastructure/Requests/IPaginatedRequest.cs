namespace Squid.Core.Requests;

public interface IPaginatedRequest : IRequest
{
    int PageIndex { get; set; }
    int PageSize { get; set; }
}