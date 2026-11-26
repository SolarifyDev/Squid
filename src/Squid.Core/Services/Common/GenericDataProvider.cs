using Microsoft.EntityFrameworkCore.Storage;
using Squid.Core.Persistence.Db;

namespace Squid.Core.Services.Common;

public interface IGenericDataProvider : IScopedDependency
{
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default);
    
    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default);
}

public class GenericDataProvider : IGenericDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public GenericDataProvider(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
    {
        using var transaction = await _repository.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            T result = await action(cancellationToken).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await RollbackQuietlyAsync(transaction, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        using var transaction = await _repository.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await action(cancellationToken).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await RollbackQuietlyAsync(transaction, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    // The rollback is best effort: when the commit itself failed (a connection dropped mid-commit, so the outcome on the server is unknown) the rollback
    // usually fails too, and its error must not replace the original one — callers classify that original failure (a transient one is retried).
    private static async Task RollbackQuietlyAsync(IDbContextTransaction transaction, CancellationToken ct)
    {
        try
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Rolling back a failed transaction failed too; surfacing the original failure");
        }
    }
}
