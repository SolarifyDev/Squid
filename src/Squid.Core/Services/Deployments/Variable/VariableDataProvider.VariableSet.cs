using Squid.Message.Enums;

namespace Squid.Core.Services.Deployments.Variable;

public partial class VariableDataProvider
{
    public async Task AddVariableSetAsync(VariableSet variableSet, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(variableSet, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UpdateVariableSetAsync(VariableSet variableSet, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.UpdateAsync(variableSet, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteVariableSetAsync(VariableSet variableSet, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteAsync(variableSet, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<(int count, List<VariableSet>)> GetVariableSetPagingAsync(VariableSetOwnerType? ownerType = null, int? ownerId = null, int? spaceId = null, string keyword = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = _repository.Query<VariableSet>();

        if (ownerType.HasValue)
        {
            query = query.Where(vs => vs.OwnerType == ownerType.Value);
        }

        if (ownerId.HasValue)
        {
            query = query.Where(vs => vs.OwnerId == ownerId.Value);
        }

        if (spaceId.HasValue)
        {
            query = query.Where(vs => vs.SpaceId == spaceId.Value);
        }

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        if (pageIndex.HasValue && pageSize.HasValue)
        {
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);
        }

        var results = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        return (count, results);
    }

    public async Task<VariableSet> GetVariableSetByIdAsync(int id, CancellationToken cancellationToken)
    {
        return await _repository.Query<VariableSet>(x => x.Id == id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }
}
