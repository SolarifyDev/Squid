using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Deployments.Variables;

public interface ILibraryVariableSetDataProvider : IScopedDependency
{
    Task AddAsync(LibraryVariableSet entity, bool forceSave = true, CancellationToken ct = default);
    Task<LibraryVariableSet> GetByIdAsync(int id, CancellationToken ct = default);
    Task<List<LibraryVariableSet>> GetByIdsAsync(List<int> ids, CancellationToken ct = default);
    Task DeleteAsync(LibraryVariableSet entity, bool forceSave = true, CancellationToken ct = default);
}

public class LibraryVariableSetDataProvider : ILibraryVariableSetDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public LibraryVariableSetDataProvider(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task AddAsync(LibraryVariableSet entity, bool forceSave = true, CancellationToken ct = default)
    {
        await _repository.InsertAsync(entity, ct).ConfigureAwait(false);

        if (forceSave)
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<LibraryVariableSet> GetByIdAsync(int id, CancellationToken ct = default)
    {
        return await _repository.Query<LibraryVariableSet>(x => x.Id == id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<LibraryVariableSet>> GetByIdsAsync(List<int> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return new List<LibraryVariableSet>();

        return await _repository.QueryNoTracking<LibraryVariableSet>(x => ids.Contains(x.Id))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(LibraryVariableSet entity, bool forceSave = true, CancellationToken ct = default)
    {
        await _repository.DeleteAsync(entity, ct).ConfigureAwait(false);

        if (forceSave)
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
