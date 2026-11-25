using FluentValidation;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Identity;
using Squid.Core.Services.Packages.Versioning.Exceptions;
using Squid.Message.Commands.PackageVersioning;
using Squid.Message.Constants;
using Squid.Message.Models.PackageVersioning;

namespace Squid.Core.Services.Packages.Versioning;

public interface IPackageVersionRuleService : IScopedDependency
{
    /// <summary>The rule of the package, or the default one (<see cref="PackageVersioning.DefaultTemplate"/>) when it has none.</summary>
    Task<PackageVersionRuleDto> GetEffectiveRuleAsync(int? spaceId, int feedId, string packageId, CancellationToken ct);

    /// <summary>Saves the rule after checking the template against a sample upload; a template that cannot give a valid version is rejected and nothing is saved.</summary>
    Task<PackageVersionRuleDto> SetRuleAsync(SetPackageVersionRuleCommand command, CancellationToken ct);

    /// <summary>The packages of the feed that have a rule of their own.</summary>
    Task<List<PackageVersionRuleDto>> ListRulesAsync(int? spaceId, int feedId, CancellationToken ct);
}

public sealed class PackageVersionRuleService(IRepository repository, IPackageVersionAssigner assigner, ICurrentUser currentUser) : IPackageVersionRuleService
{
    public async Task<PackageVersionRuleDto> GetEffectiveRuleAsync(int? spaceId, int feedId, string packageId, CancellationToken ct)
    {
        await EnsureFeedAsync(spaceId, feedId, ct).ConfigureAwait(false);

        var rule = await repository.QueryNoTracking<PackageVersionRule>(r => r.FeedId == feedId && r.PackageId == packageId).FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return rule == null ? DefaultRule(feedId, packageId) : ToDto(rule);
    }

    public async Task<PackageVersionRuleDto> SetRuleAsync(SetPackageVersionRuleCommand command, CancellationToken ct)
    {
        await EnsureFeedAsync(command.SpaceId, command.FeedId, ct).ConfigureAwait(false);

        var packageId = RequirePackageId(command.PackageId);
        var template = command.VersionTemplate?.Trim();

        await assigner.PreviewAsync(template, PackageVersionSampleContext.Create(command.FeedId, packageId), ct).ConfigureAwait(false);
        await UpsertAsync(command.FeedId, packageId, template, ct).ConfigureAwait(false);

        return new PackageVersionRuleDto { FeedId = command.FeedId, PackageId = packageId, VersionTemplate = template };
    }

    public async Task<List<PackageVersionRuleDto>> ListRulesAsync(int? spaceId, int feedId, CancellationToken ct)
    {
        await EnsureFeedAsync(spaceId, feedId, ct).ConfigureAwait(false);

        var rules = await repository.QueryNoTracking<PackageVersionRule>(r => r.FeedId == feedId).OrderBy(r => r.PackageId).ToListAsync(ct).ConfigureAwait(false);

        return rules.Select(ToDto).ToList();
    }

    // A feed of another space is reported as missing, so a caller cannot probe it by changing the space id.
    private async Task EnsureFeedAsync(int? spaceId, int feedId, CancellationToken ct)
    {
        var feed = await repository.QueryNoTracking<ExternalFeed>(f => f.Id == feedId).Select(f => new { f.SpaceId }).FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (feed == null || (spaceId.HasValue && feed.SpaceId != spaceId.Value))
            throw new PackageVersionFeedNotFoundException(feedId);
    }

    private static string RequirePackageId(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            throw new ValidationException("PackageId is required");

        if (packageId.Length > 255)
            throw new ValidationException("PackageId is longer than 255 characters");

        return packageId.Trim();
    }

    private async Task UpsertAsync(int feedId, string packageId, string template, CancellationToken ct)
    {
        var userId = currentUser?.Id ?? CurrentUsers.InternalUser.Id;

        await repository.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO package_version_rule (feed_id, package_id, version_template, created_by, last_modified_by) VALUES ({feedId}, {packageId}, {template}, {userId}, {userId})
            ON CONFLICT (feed_id, package_id) DO UPDATE SET version_template = EXCLUDED.version_template, last_modified_date = now(), last_modified_by = EXCLUDED.last_modified_by
            """, ct).ConfigureAwait(false);
    }

    private static PackageVersionRuleDto DefaultRule(int feedId, string packageId)
    {
        return new PackageVersionRuleDto { FeedId = feedId, PackageId = packageId, VersionTemplate = PackageVersioning.DefaultTemplate, IsDefault = true };
    }

    private static PackageVersionRuleDto ToDto(PackageVersionRule rule)
    {
        return new PackageVersionRuleDto { FeedId = rule.FeedId, PackageId = rule.PackageId, VersionTemplate = rule.VersionTemplate };
    }
}
