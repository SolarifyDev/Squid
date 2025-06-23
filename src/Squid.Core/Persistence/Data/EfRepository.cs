using Squid.Message.Domain;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Squid.Core.Persistence.Data;

public class EfRepository : IRepository
{
    private readonly SquidDbContext _dbContext;

    public EfRepository(SquidDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public ValueTask<TEntity> GetByIdAsync<TEntity>(object id,
        CancellationToken cancellationToken = default) where TEntity : class, IEntity
    {
        return _dbContext.FindAsync<TEntity>(new object[] { id }, cancellationToken);
    }

    public Task<List<TEntity>> GetAllAsync<TEntity>(CancellationToken cancellationToken = default)
        where TEntity : class, IEntity
    {
        return _dbContext.Set<TEntity>().ToListAsync(cancellationToken);
    }

    public Task<List<TEntity>> ToListAsync<TEntity>(Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default) where TEntity : class, IEntity
    {
        return _dbContext.Set<TEntity>().Where(predicate).ToListAsync(cancellationToken);
    }

    public async Task InsertAsync<TEntity>(TEntity entity,
        CancellationToken cancellationToken = default) where TEntity : class, IEntity
    {
        await _dbContext.AddAsync(entity, cancellationToken).ConfigureAwait(false);
        _dbContext.ShouldSaveChanges = true;
    }

    public async Task InsertAllAsync<TEntity>(IEnumerable<TEntity> entities,
        CancellationToken cancellationToken = default) where TEntity : class, IEntity
    {
        await _dbContext.AddRangeAsync(entities, cancellationToken).ConfigureAwait(false);
        _dbContext.ShouldSaveChanges = true;
    }

    public Task UpdateAsync<TEntity>(TEntity entity,
        CancellationToken cancellationToken = default) where TEntity : class, IEntity
    {
        _dbContext.Update(entity);
        _dbContext.ShouldSaveChanges = true;
        return Task.CompletedTask;
    }

    public Task UpdateAllAsync<TEntity>(IEnumerable<TEntity> entities,
        CancellationToken cancellationToken = default) where TEntity : class, IEntity
    {
        _dbContext.UpdateRange(entities);
        _dbContext.ShouldSaveChanges = true;
        return Task.CompletedTask;
    }

    public Task DeleteAsync<TEntity>(TEntity entity,
        CancellationToken cancellationToken = default) where TEntity : class, IEntity
    {
        _dbContext.Remove(entity);
        _dbContext.ShouldSaveChanges = true;
        return Task.CompletedTask;
    }

    public Task DeleteAllAsync<TEntity>(IEnumerable<TEntity> entities,
        CancellationToken cancellationToken = default) where TEntity : class, IEntity
    {
        _dbContext.RemoveRange(entities);
        _dbContext.ShouldSaveChanges = true;
        return Task.CompletedTask;
    }

    public Task<int> CountAsync<TEntity>(Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default) where TEntity : class, IEntity
    {
        return _dbContext.Set<TEntity>().CountAsync(predicate, cancellationToken);
    }

    public Task<TEntity?> SingleOrDefaultAsync<TEntity>(Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default) where TEntity : class, IEntity
    {
        return _dbContext.Set<TEntity>().SingleOrDefaultAsync(predicate, cancellationToken);
    }

    public Task<TEntity?> FirstOrDefaultAsync<TEntity>(Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default) where TEntity : class, IEntity
    {
        return _dbContext.Set<TEntity>().FirstOrDefaultAsync(predicate, cancellationToken);
    }

    public Task<bool> AnyAsync<TEntity>(Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default) where TEntity : class, IEntity
    {
        return _dbContext.Set<TEntity>().AnyAsync(predicate, cancellationToken);
    }

    public async Task<List<TEntity>> SqlQueryAsync<TEntity>(string sql, params object[] parameters) where TEntity : class, IEntity
    {
        return await _dbContext.Set<TEntity>().FromSqlRaw(sql, parameters).ToListAsync();
    }

    public IQueryable<TEntity> Query<TEntity>(Expression<Func<TEntity, bool>>? predicate = null)
        where TEntity : class, IEntity
    {
        return predicate == null ? _dbContext.Set<TEntity>() : _dbContext.Set<TEntity>().Where(predicate);
    }

    public IQueryable<TEntity> QueryNoTracking<TEntity>(Expression<Func<TEntity, bool>>? predicate = null)
        where TEntity : class, IEntity
    {
        return predicate == null
            ? _dbContext.Set<TEntity>().AsNoTracking()
            : _dbContext.Set<TEntity>().AsNoTracking().Where(predicate);
    }

    public DatabaseFacade Database => _dbContext.Database;
}