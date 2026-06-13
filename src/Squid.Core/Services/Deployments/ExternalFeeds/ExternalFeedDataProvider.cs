using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Services.Security;

namespace Squid.Core.Services.Deployments.ExternalFeeds;

public interface IExternalFeedDataProvider : IScopedDependency
{
    Task AddExternalFeedAsync(Persistence.Entities.Deployments.ExternalFeed externalFeed, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdateExternalFeedAsync(Persistence.Entities.Deployments.ExternalFeed externalFeed, bool forceSave = true, CancellationToken cancellationToken = default);

    Task DeleteExternalFeedsAsync(List<Persistence.Entities.Deployments.ExternalFeed> externalFeeds, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<(int count, List<Persistence.Entities.Deployments.ExternalFeed>)> GetExternalFeedPagingAsync(int? spaceId = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);

    Task<List<Persistence.Entities.Deployments.ExternalFeed>> GetExternalFeedsByIdsAsync(List<int> ids, CancellationToken cancellationToken);

    Task<Persistence.Entities.Deployments.ExternalFeed> GetFeedByIdAsync(int feedId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns at-rest protection of <see cref="Persistence.Entities.Deployments.ExternalFeed.Password"/>
/// — the package-feed credential, previously persisted in cleartext. The single seam every feed
/// read and write funnels through (deploy-pipeline package fetch, Helm/K8s registry auth, the
/// feed service), so encrypting here covers every consumer transparently — same pattern as
/// <c>DeploymentAccountDataProvider</c> / <c>VariableDataProvider</c>.
///
/// <para>Read-both / non-breaking: <see cref="IVariableEncryptionService.EncryptAsync"/> always
/// emits the <c>SQUID_ENCRYPTED_V2:</c> envelope; <c>DecryptAsync</c> returns any value lacking
/// that prefix verbatim, so pre-existing plaintext rows still load and upgrade lazily on next save.
/// Reads use the repository's AsNoTracking query (<c>QueryNoTracking</c>) so decrypting the
/// in-memory entity can never be flushed back to the DB as plaintext by a later shared-scope
/// SaveChanges.</para>
/// </summary>
public class ExternalFeedDataProvider(IUnitOfWork unitOfWork, IRepository repository, IVariableEncryptionService encryption) : IExternalFeedDataProvider
{
    // The V2 envelope derives its key from a random per-payload salt, so the legacy KDF-scope
    // argument (a V1-only deterministic-salt input) is irrelevant for feeds, which only write V2.
    private const int PasswordKdfScope = 0;

    public async Task AddExternalFeedAsync(Persistence.Entities.Deployments.ExternalFeed externalFeed, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        EncryptPassword(externalFeed);

        await repository.InsertAsync(externalFeed, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateExternalFeedAsync(Persistence.Entities.Deployments.ExternalFeed externalFeed, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        EncryptPassword(externalFeed);

        await repository.UpdateAsync(externalFeed, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteExternalFeedsAsync(List<Persistence.Entities.Deployments.ExternalFeed> externalFeeds, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.DeleteAllAsync(externalFeeds, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(int count, List<Persistence.Entities.Deployments.ExternalFeed>)> GetExternalFeedPagingAsync(int? spaceId = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = repository.QueryNoTracking<Persistence.Entities.Deployments.ExternalFeed>();

        if (spaceId.HasValue)
            query = query.Where(f => f.SpaceId == spaceId.Value);

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        if (pageIndex.HasValue && pageSize.HasValue)
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);

        var feeds = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        return (count, await DecryptPasswordsAsync(feeds).ConfigureAwait(false));
    }

    public async Task<List<Persistence.Entities.Deployments.ExternalFeed>> GetExternalFeedsByIdsAsync(List<int> ids, CancellationToken cancellationToken)
    {
        var feeds = await repository.QueryNoTracking<Persistence.Entities.Deployments.ExternalFeed>()
            .Where(f => ids.Contains(f.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return await DecryptPasswordsAsync(feeds).ConfigureAwait(false);
    }

    public async Task<Persistence.Entities.Deployments.ExternalFeed> GetFeedByIdAsync(int feedId, CancellationToken cancellationToken = default)
    {
        // QueryNoTracking so the decrypt below mutates a DETACHED entity — a tracked entity would
        // be detected as Modified and a later in-scope SaveChanges would flush plaintext back.
        var feed = await repository.QueryNoTracking<Persistence.Entities.Deployments.ExternalFeed>(f => f.Id == feedId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return await DecryptPasswordAsync(feed).ConfigureAwait(false);
    }

    // Encrypt only if not already an envelope — idempotent, so a re-save never double-wraps.
    private void EncryptPassword(Persistence.Entities.Deployments.ExternalFeed feed)
    {
        if (feed == null || string.IsNullOrEmpty(feed.Password)) return;
        if (encryption.IsValidEncryptedValue(feed.Password)) return;

        feed.Password = encryption.EncryptAsync(feed.Password, PasswordKdfScope);
    }

    private async Task<Persistence.Entities.Deployments.ExternalFeed> DecryptPasswordAsync(Persistence.Entities.Deployments.ExternalFeed feed)
    {
        if (feed == null || string.IsNullOrEmpty(feed.Password)) return feed;

        // Read-both: a plaintext (unprefixed) value is returned verbatim by DecryptAsync.
        feed.Password = await encryption.DecryptAsync(feed.Password, PasswordKdfScope).ConfigureAwait(false);

        return feed;
    }

    private async Task<List<Persistence.Entities.Deployments.ExternalFeed>> DecryptPasswordsAsync(List<Persistence.Entities.Deployments.ExternalFeed> feeds)
    {
        foreach (var feed in feeds)
            await DecryptPasswordAsync(feed).ConfigureAwait(false);

        return feeds;
    }
}
