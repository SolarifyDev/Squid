using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Enums.Deployments;

namespace Squid.Core.Services.Deployments.ServerTask;

public interface IServerTaskLogDataProvider : IScopedDependency
{
    Task AddLogAsync(ServerTaskLog log, bool forceSave = true, CancellationToken ct = default);

    Task AddLogsAsync(List<ServerTaskLog> logs, bool forceSave = true, CancellationToken ct = default);

    Task<List<ServerTaskLog>> GetLogsByTaskIdAsync(int serverTaskId, CancellationToken ct = default);

    Task<List<ServerTaskLog>> GetLogsByTaskIdAndCategoryAsync(int serverTaskId, string category, CancellationToken ct = default);

    Task<long> GetLogCountByTaskIdAsync(int serverTaskId, CancellationToken ct = default);
}

public class ServerTaskLogDataProvider : IServerTaskLogDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public ServerTaskLogDataProvider(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task AddLogAsync(ServerTaskLog log, bool forceSave = true, CancellationToken ct = default)
    {
        await _repository.InsertAsync(log, ct).ConfigureAwait(false);

        if (forceSave)
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task AddLogsAsync(List<ServerTaskLog> logs, bool forceSave = true, CancellationToken ct = default)
    {
        if (logs == null || logs.Count == 0)
            return;

        await _repository.InsertAllAsync(logs, ct).ConfigureAwait(false);

        if (forceSave)
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<ServerTaskLog>> GetLogsByTaskIdAsync(int serverTaskId, CancellationToken ct = default)
    {
        return await _repository.QueryNoTracking<ServerTaskLog>(l => l.ServerTaskId == serverTaskId)
            .OrderBy(l => l.SequenceNumber)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<ServerTaskLog>> GetLogsByTaskIdAndCategoryAsync(int serverTaskId, string category, CancellationToken ct = default)
    {
        if (!Enum.TryParse<ServerTaskLogCategory>(category, ignoreCase: true, out var parsedCategory))
            return [];

        return await _repository.QueryNoTracking<ServerTaskLog>(l =>
                l.ServerTaskId == serverTaskId && l.Category == parsedCategory)
            .OrderBy(l => l.SequenceNumber)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<long> GetLogCountByTaskIdAsync(int serverTaskId, CancellationToken ct = default)
    {
        return await _repository.CountAsync<ServerTaskLog>(l => l.ServerTaskId == serverTaskId, ct).ConfigureAwait(false);
    }
}
