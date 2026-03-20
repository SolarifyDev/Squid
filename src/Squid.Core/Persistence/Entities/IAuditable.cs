namespace Squid.Core.Persistence.Entities;

public interface IAuditable
{
    DateTimeOffset CreatedDate { get; set; }
    int CreatedBy { get; set; }
    DateTimeOffset LastModifiedDate { get; set; }
    int LastModifiedBy { get; set; }
}
