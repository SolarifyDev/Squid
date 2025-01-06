using Squid.Core.Domain.Deployments;

namespace Squid.Core;

public interface ISquidDbContext
{
    DbSet<Customer> Customers { get; set; }
    DbSet<Deployment> Deployments { get; set; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}