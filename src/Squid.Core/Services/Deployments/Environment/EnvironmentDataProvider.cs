namespace Squid.Core.Services.Deployments.Environment;

public interface IEnvironmentDataProvider : IScopedDependency
{
    Task AddEnvironmentAsync(Message.Domain.Deployments.Environment environment, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdateEnvironmentAsync(Message.Domain.Deployments.Environment environment, bool forceSave = true, CancellationToken cancellationToken = default);

    Task DeleteEnvironmentsAsync(List<Message.Domain.Deployments.Environment> environments, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<(int count, List<Message.Domain.Deployments.Environment>)> GetEnvironmentPagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);

    Task<List<Message.Domain.Deployments.Environment>> GetEnvironmentsByIdsAsync(List<Guid> ids, CancellationToken cancellationToken);
}

public class EnvironmentDataProvider : IEnvironmentDataProvider
{
    private readonly IUnitOfWork _unitOfWork;

    private readonly IRepository _repository;

    private readonly IMapper _mapper;

    public EnvironmentDataProvider(IUnitOfWork unitOfWork, IRepository repository, IMapper mapper)
    {
        _unitOfWork = unitOfWork;
        _repository = repository;
        _mapper = mapper;
    }

    public async Task AddEnvironmentAsync(Message.Domain.Deployments.Environment environment, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(environment, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UpdateEnvironmentAsync(Message.Domain.Deployments.Environment environment, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.UpdateAsync(environment, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteEnvironmentsAsync(List<Message.Domain.Deployments.Environment> environments, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteAllAsync(environments, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<(int count, List<Message.Domain.Deployments.Environment>)> GetEnvironmentPagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = _repository.Query<Message.Domain.Deployments.Environment>();

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        if (pageIndex.HasValue && pageSize.HasValue)
        {
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);
        }

        return (count, await query.ToListAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<List<Message.Domain.Deployments.Environment>> GetEnvironmentsByIdsAsync(List<Guid> ids, CancellationToken cancellationToken)
    {
        return await _repository.Query<Message.Domain.Deployments.Environment>(x => ids.Contains(x.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
