namespace Squid.Core.Persistence.Db;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    bool ShouldSaveChanges { get; set; }
}