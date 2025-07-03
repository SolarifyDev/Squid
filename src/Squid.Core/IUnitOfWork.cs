namespace Squid.Core;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    bool ShouldSaveChanges { get; set; }
}