using System.Data;
using Microsoft.EntityFrameworkCore.Storage;
using Squid.Core.Persistence.Db;

namespace Squid.Core.Services.OctopusImport;

public interface IOctopusImportTransactionExecutor : IScopedDependency
{
    Task ExecuteInImportTransactionAsync(
        OctopusImportTransactionContext context,
        Func<OctopusImportTransactionContext, CancellationToken, Task> action,
        CancellationToken ct = default);

    Task<T> ExecuteInImportTransactionAsync<T>(
        OctopusImportTransactionContext context,
        Func<OctopusImportTransactionContext, CancellationToken, Task<T>> action,
        CancellationToken ct = default);
}

public sealed class OctopusImportTransactionExecutor : IOctopusImportTransactionExecutor
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public OctopusImportTransactionExecutor(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    /// <summary>
    /// Opens one database transaction for the whole confirmation scope, runs the caller's work
    /// inside that boundary, flushes pending save changes, and commits only when the work
    /// completes successfully.
    /// </summary>
    public async Task ExecuteInImportTransactionAsync(
        OctopusImportTransactionContext context,
        Func<OctopusImportTransactionContext, CancellationToken, Task> action,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(action);

        using var transaction = await BeginTransactionAsync(context, ct).ConfigureAwait(false);
        try
        {
            await action(context, ct).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            _repository.ClearChangeTracker();
            throw;
        }
    }

    public async Task<T> ExecuteInImportTransactionAsync<T>(
        OctopusImportTransactionContext context,
        Func<OctopusImportTransactionContext, CancellationToken, Task<T>> action,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(action);

        using var transaction = await BeginTransactionAsync(context, ct).ConfigureAwait(false);
        try
        {
            var result = await action(context, ct).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            _repository.ClearChangeTracker();
            throw;
        }
    }

    private Task<IDbContextTransaction> BeginTransactionAsync(OctopusImportTransactionContext context, CancellationToken ct)
    {
        return context.IsolationLevel.HasValue
            ? _repository.Database.BeginTransactionAsync(context.IsolationLevel.Value, ct)
            : _repository.Database.BeginTransactionAsync(ct);
    }
}
