using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;

namespace Squid.Core.Services.Authorization;

public interface IUserRoleDataProvider : IScopedDependency
{
    Task<UserRole> GetByIdAsync(int id, CancellationToken ct = default);
    Task<UserRole> GetByNameAsync(string name, CancellationToken ct = default);
    Task<List<UserRole>> GetAllAsync(CancellationToken ct = default);
    Task<List<string>> GetPermissionsAsync(int userRoleId, CancellationToken ct = default);
    Task AddAsync(UserRole role, bool forceSave = true, CancellationToken ct = default);
    Task UpdateAsync(UserRole role, bool forceSave = true, CancellationToken ct = default);
    Task DeleteAsync(UserRole role, bool forceSave = true, CancellationToken ct = default);
    Task SetPermissionsAsync(int userRoleId, List<string> permissions, CancellationToken ct = default);
}

public class UserRoleDataProvider(IUnitOfWork unitOfWork, IRepository repository) : IUserRoleDataProvider
{
    public async Task<UserRole> GetByIdAsync(int id, CancellationToken ct = default)
    {
        return await repository.GetByIdAsync<UserRole>(id, ct).ConfigureAwait(false);
    }

    public async Task<UserRole> GetByNameAsync(string name, CancellationToken ct = default)
    {
        return await repository.FirstOrDefaultAsync<UserRole>(x => x.Name == name, ct).ConfigureAwait(false);
    }

    public async Task<List<UserRole>> GetAllAsync(CancellationToken ct = default)
    {
        return await repository.GetAllAsync<UserRole>(ct).ConfigureAwait(false);
    }

    public async Task<List<string>> GetPermissionsAsync(int userRoleId, CancellationToken ct = default)
    {
        return await repository.Query<UserRolePermission>(x => x.UserRoleId == userRoleId)
            .Select(x => x.Permission).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task AddAsync(UserRole role, bool forceSave = true, CancellationToken ct = default)
    {
        await repository.InsertAsync(role, ct).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(UserRole role, bool forceSave = true, CancellationToken ct = default)
    {
        await repository.UpdateAsync(role, ct).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(UserRole role, bool forceSave = true, CancellationToken ct = default)
    {
        await repository.DeleteAsync(role, ct).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SetPermissionsAsync(int userRoleId, List<string> permissions, CancellationToken ct = default)
    {
        await repository.ExecuteDeleteAsync<UserRolePermission>(x => x.UserRoleId == userRoleId, ct).ConfigureAwait(false);

        var entities = permissions.Select(p => new UserRolePermission { UserRoleId = userRoleId, Permission = p }).ToList();

        if (entities.Count > 0)
            await repository.InsertAllAsync(entities, ct).ConfigureAwait(false);

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
