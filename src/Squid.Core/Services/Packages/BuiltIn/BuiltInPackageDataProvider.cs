using Microsoft.EntityFrameworkCore;
using Npgsql;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Core.Services.Packages.Storage;
using Squid.Message.Models.BuiltInPackages;

namespace Squid.Core.Services.Packages.BuiltIn;

// Package ids are ASCII (see BuiltInPackageIds), so every lowering of an id done here in C# uses the invariant culture and agrees with lower() in the database.
public class BuiltInPackageDataProvider(IRepository repository, IUnitOfWork unitOfWork) : IBuiltInPackageDataProvider, IPackageStoreUsage
{
    public async Task<string> ResolveCanonicalIdAsync(int feedId, string packageId, CancellationToken ct)
    {
        await repository.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO builtin_package_name (feed_id, package_id) VALUES ({feedId}, {packageId}) ON CONFLICT (feed_id, lower(package_id)) DO NOTHING", ct).ConfigureAwait(false);

        return await repository.Database.SqlQuery<string>($"SELECT package_id AS \"Value\" FROM builtin_package_name WHERE feed_id = {feedId} AND lower(package_id) = lower({packageId})").SingleAsync(ct).ConfigureAwait(false);
    }

    public Task<BuiltInPackage> GetAsync(int feedId, string packageId, string version, CancellationToken ct)
    {
        var lowered = packageId.ToLowerInvariant();

        return repository.QueryNoTracking<BuiltInPackage>(p => p.FeedId == feedId && p.PackageId.ToLower() == lowered && p.Version == version).FirstOrDefaultAsync(ct);
    }

    public Task<List<BuiltInPackage>> ListVersionsAsync(int feedId, string packageId, CancellationToken ct)
    {
        var lowered = packageId.ToLowerInvariant();

        return repository.QueryNoTracking<BuiltInPackage>(p => p.FeedId == feedId && p.PackageId.ToLower() == lowered).OrderByDescending(p => p.PublishedDate).ThenByDescending(p => p.Id).ToListAsync(ct);
    }

    public async Task<List<BuiltInPackageDto>> ListPackagesAsync(int feedId, string filter, int take, CancellationToken ct)
    {
        var lowered = filter?.Trim().ToLowerInvariant() ?? string.Empty;

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

    // The release check is part of the UPDATE itself, so a release that selected this version while the (possibly long) file write was going on still stops the
    // overwrite, which a check made before the write cannot do. The predicate is the one of IBuiltInPackageUsage: both take it from ReleaseSelectionSql.
    public async Task ReplaceContentAsync(int id, BuiltInPackage content, CancellationToken ct)
    {
        var notSelected = $"NOT {ReleaseSelectionSql.Selects("builtin_package.feed_id", "builtin_package.package_id", "builtin_package.version")}";

        var updated = await repository.Database.ExecuteSqlRawAsync($$"""
            UPDATE builtin_package SET file_extension = {0}, size_bytes = {1}, sha256 = {2}, content_fingerprint = {3},
                store_id = {4}, blob_key = {5}, published_date = {6}, published_by = {7}, source = {8},
                facts = {9}::jsonb
            WHERE id = {10} AND {{notSelected}}
            """, [content.FileExtension, content.SizeBytes, content.Sha256, content.ContentFingerprint, content.StoreId, content.BlobKey, content.PublishedDate, content.PublishedBy, content.Source, content.Facts, id], ct).ConfigureAwait(false);

        if (updated == 0)
            throw new PackageVersionInUseException(content.PackageId, content.Version);
    }

    public Task<bool> HoldsFilesAsync(int storeId, CancellationToken ct)
    {
        return repository.QueryNoTracking<BuiltInPackage>(p => p.StoreId == storeId).AnyAsync(ct);
    }
}
