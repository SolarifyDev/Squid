namespace Squid.Core;

public interface IApplicationDbContext
{
    DbSet<Customer> Customers { get; set; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}