using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;

namespace Squid.Core.Services.Packages.BuiltIn;

/// <summary>
/// The one definition of "a release relies on this built-in package version", as a SQL predicate, so the early check of a push and the atomic UPDATE of a replace can
/// never disagree. A <c>release_selected_package</c> row only records the feed, the version, the action name and a free-text reference name: the package id itself lives
/// in the process step's property, which may be a variable (<c>#{AppPackage}</c>) that is expanded at deploy time, and a release created through the API may send any
/// reference name at all. So the reference name is trusted only when it names a package: a row selecting the version in the feed counts for the package when its
/// reference name is the package's id, or when it is not the id of any built-in package of the feed (empty, a variable, a typo: it could be this package, and a release
/// is a promise that its bytes stay as they were). A reference name that is another built-in package's id is that package's selection.
/// </summary>
internal static class ReleaseSelectionSql
{
    /// <summary>
    /// A boolean SQL expression, true when a release selects the version. The three arguments are SQL expressions (column names or <c>{n}</c> placeholders of
    /// <c>ExecuteSqlRaw</c> / <c>SqlQueryRaw</c>) for the feed id, the package id and the version.
    /// </summary>
    public static string Selects(string feedId, string packageId, string version)
    {
        return $"""
            EXISTS (
                SELECT 1 FROM release_selected_package r
                WHERE r.feed_id = {feedId} AND r.version = {version}
                  AND (lower(r.package_reference_name) = lower({packageId})
                       OR NOT EXISTS (SELECT 1 FROM builtin_package n WHERE n.feed_id = r.feed_id AND lower(n.package_id) = lower(r.package_reference_name))))
            """;
    }
}

public sealed class BuiltInPackageUsage(IRepository repository) : IBuiltInPackageUsage
{
    public Task<bool> IsVersionSelectedByReleaseAsync(int feedId, string packageId, string version, CancellationToken ct)
    {
        return repository.Database.SqlQueryRaw<bool>($"SELECT {ReleaseSelectionSql.Selects("{0}", "{1}", "{2}")} AS \"Value\"", feedId, packageId, version).SingleAsync(ct);
    }
}
