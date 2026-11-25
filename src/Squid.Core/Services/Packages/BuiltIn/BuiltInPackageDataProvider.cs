using Microsoft.EntityFrameworkCore;
using Npgsql;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Core.Services.Packages.Storage;
using Squid.Message.Models.BuiltInPackages;

namespace Squid.Core.Services.Packages.BuiltIn;

public class BuiltInPackageDataProvider(IRepository repository, IUnitOfWork unitOfWork) : IBuiltInPackageDataProvider, IPackageStoreUsage
{
    public async Task<string> ResolveCanonicalIdAsync(int feedId, string packageId, CancellationToken ct)
    {
        await repository.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO builtin_package_name (feed_id, package_id) VALUES ({feedId}, {packageId}) ON CONFLICT (feed_id, lower(package_id)) DO NOTHING", ct).ConfigureAwait(false);

        return await repository.Database.SqlQuery<string>($"SELECT package_id AS \"Value\" FROM builtin_package_name WHERE feed_id = {feedId} AND lower(package_id) = lower({packageId})").SingleAsync(ct).ConfigureAwait(false);
    }

    public Task<BuiltInPackage> GetAsync(int feedId, string packageId, string version, CancellationToken ct)
    {
        var lowered = packageId.ToLower();

        return repository.QueryNoTracking<BuiltInPackage>(p => p.FeedId == feedId && p.PackageId.ToLower() == lowered && p.Version == version).FirstOrDefaultAsync(ct);
    }

    public Task<List<BuiltInPackage>> ListVersionsAsync(int feedId, string packageId, CancellationToken ct)
    {
        var lowered = packageId.ToLower();

        return repository.QueryNoTracking<BuiltInPackage>(p => p.FeedId == feedId && p.PackageId.ToLower() == lowered).OrderByDescending(p => p.PublishedDate).ThenByDescending(p => p.Id).ToListAsync(ct);
    }

    public async Task<List<BuiltInPackageDto>> ListPackagesAsync(int feedId, string filter, int take, CancellationToken ct)
    {
        var lowered = filter?.Trim().ToLower() ?? string.Empty;

        return await repository.QueryNoTracking<BuiltInPackage>(p => p.FeedId == feedId && p.PackageId.ToLower().Contains(lowered))
            .GroupBy(p => p.PackageId)
            .Select(g => new BuiltInPackageDto { PackageId = g.Key, VersionCount = g.Count(), LastPublishedDate = g.Max(p => p.PublishedDate) })
            .OrderBy(p => p.PackageId)
            .Take(take)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task InsertAsync(BuiltInPackage package, CancellationToken ct)
    {
        await repository.InsertAsync(package, ct).ConfigureAwait(false);

        try
        {
            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new PackageVersionAlreadyExistsException(package.PackageId, package.Version);
        }
    }

    public Task ReplaceContentAsync(int id, BuiltInPackage content, CancellationToken ct)
    {
        return repository.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE builtin_package SET file_extension = {content.FileExtension}, size_bytes = {content.SizeBytes}, sha256 = {content.Sha256}, store_id = {content.StoreId}, blob_key = {content.BlobKey},
                published_date = {content.PublishedDate}, published_by = {content.PublishedBy}, source = {content.Source},
                facts = {content.Facts}::jsonb
            WHERE id = {id}
            """, ct);
    }

    public Task<bool> HoldsFilesAsync(int storeId, CancellationToken ct)
    {
        return repository.QueryNoTracking<BuiltInPackage>(p => p.StoreId == storeId).AnyAsync(ct);
    }
}
