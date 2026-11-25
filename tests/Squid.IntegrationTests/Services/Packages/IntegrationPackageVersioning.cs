using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Packages.Versioning;
using Squid.Core.Services.Packages.Versioning.Exceptions;
using Squid.Message.Commands.PackageVersioning;
using Squid.Message.Constants;
using Squid.Message.Models.PackageVersioning;

namespace Squid.IntegrationTests.Services.Packages;

/// <summary>
/// Integration tier on the real Postgres and the real migration: the counter the {counter} token takes numbers from (atomic, never duplicated, gaps allowed),
/// the rule storage and its default, and the container wiring that lets the assigner find every token source.
/// </summary>
public class IntegrationPackageVersioning : TestBase
{
    private const string Package = "Acme.Web";

    public IntegrationPackageVersioning() : base("PackageVersioning", "squid_it_package_versioning")
    {
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static Action<ContainerBuilder> ClockAt(string utc) => builder => builder.RegisterInstance<TimeProvider>(new TestClock(DateTimeOffset.Parse(utc))).SingleInstance();

    private static VersionTokenContext Upload(int feedId, string package = Package) => new() { FeedId = feedId, PackageId = package };

    private async Task<int> SeedFeedAsync(int spaceId = 1)
    {
        var id = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var feed = new ExternalFeed { SpaceId = spaceId, Name = $"feed-{Guid.NewGuid():N}"[..20], Slug = $"feed-{Guid.NewGuid():N}", FeedType = "Docker", FeedUri = "https://registry.example.com" };

            await repository.InsertAsync(feed).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            id = feed.Id;
        }).ConfigureAwait(false);

        return id;
    }

    private Task<string> AssignAsync(int feedId, string template, string package = Package, Action<ContainerBuilder> registration = null)
    {
        return Run<IPackageVersionAssigner, string>(assigner => assigner.AssignAsync(template, Upload(feedId, package), CancellationToken.None), registration);
    }

    // The statement selects one bigint column; EF reads it as "Value".
    private Task<long> RowCountAsync(string sql)
    {
        return Run<IRepository, long>(repository => repository.Database.SqlQueryRaw<long>(sql.Replace("FROM", "AS \"Value\" FROM", StringComparison.Ordinal)).SingleAsync());
    }

    // ── Wiring ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryTokenSourceInTheAssembly_IsResolvedFromTheContainer()
    {
        var registered = CurrentScope.Resolve<IEnumerable<IVersionTokenSource>>().Select(s => s.GetType()).ToHashSet();
        var defined = typeof(IVersionTokenSource).Assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IVersionTokenSource).IsAssignableFrom(t)).ToHashSet();

        defined.Count.ShouldBeGreaterThanOrEqualTo(6);
        registered.ShouldBe(defined, ignoreOrder: true);
    }

    [Theory]
    [InlineData("1.0.{counter}", "1.0.1")]
    [InlineData("{date:yyyy.MM.dd}.{counter:daily}", "2026.11.25.1")]
    [InlineData("{file.version}", "1.2.3")]
    [InlineData("{source.version}", "1.2.3")]
    [InlineData("1.0.0-{git.sha}", "1.0.0-0123456")]
    [InlineData("{input}", "1.2.3")]
    public async Task TheAssignerFindsEveryTokenThroughTheContainer(string template, string expected)
    {
        var feedId = await SeedFeedAsync();

        var version = await Run<IPackageVersionAssigner, string>(a => a.PreviewAsync(template, PackageVersionSampleContext.Create(feedId, Package), CancellationToken.None), ClockAt("2026-11-25T08:00:00Z"));

        version.ShouldBe(expected);
    }

    // ── Counter ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Counter_CountsOneTwoThree_PerScope()
    {
        var feedId = await SeedFeedAsync();

        (await AssignAsync(feedId, "1.0.{counter}")).ShouldBe("1.0.1");
        (await AssignAsync(feedId, "1.0.{counter}")).ShouldBe("1.0.2");
        (await AssignAsync(feedId, "1.0.{counter}")).ShouldBe("1.0.3");
    }

    [Fact]
    public async Task Counter_PackagesTemplatesFeedsAndDays_AreIndependent()
    {
        var feedA = await SeedFeedAsync();
        var feedB = await SeedFeedAsync();

        (await AssignAsync(feedA, "1.0.{counter}")).ShouldBe("1.0.1");
        (await AssignAsync(feedA, "1.0.{counter}", "Other.Package")).ShouldBe("1.0.1");
        (await AssignAsync(feedA, "2.0.{counter}")).ShouldBe("2.0.1");
        (await AssignAsync(feedB, "1.0.{counter}")).ShouldBe("1.0.1");
        (await AssignAsync(feedA, "1.0.{counter}")).ShouldBe("1.0.2");

        (await AssignAsync(feedA, "{date}.{counter:daily}", registration: ClockAt("2026-11-25T23:59:00Z"))).ShouldBe("2026.11.25.1");
        (await AssignAsync(feedA, "{date}.{counter:daily}", registration: ClockAt("2026-11-25T23:59:30Z"))).ShouldBe("2026.11.25.2");
        (await AssignAsync(feedA, "{date}.{counter:daily}", registration: ClockAt("2026-11-26T00:00:01Z"))).ShouldBe("2026.11.26.1");
        (await AssignAsync(feedA, "1.0.{counter:daily}", registration: ClockAt("2026-11-26T00:00:01Z"))).ShouldBe("1.0.1");
    }

    [Fact]
    public async Task Counter_ParallelAssignmentsOfOneScope_AllGetDistinctNumbers()
    {
        const int parallel = 40;
        var feedId = await SeedFeedAsync();

        var versions = await Task.WhenAll(Enumerable.Range(0, parallel).Select(_ => Task.Run(() => AssignAsync(feedId, "1.0.{counter}"))));

        versions.Distinct().Count().ShouldBe(parallel, "two uploads were given the same version");
        versions.OrderBy(v => int.Parse(v[4..])).ShouldBe(Enumerable.Range(1, parallel).Select(n => $"1.0.{n}"), "the numbers are consecutive, none skipped");
    }

    [Fact]
    public async Task Counter_AFailedAssignment_ConsumesNothing_AndNeverCausesADuplicate()
    {
        var feedId = await SeedFeedAsync();

        (await AssignAsync(feedId, "1.0.{counter}")).ShouldBe("1.0.1");

        await Should.ThrowAsync<VersionTokenValueMissingException>(() => AssignAsync(feedId, "1.0.{counter}-{git.sha}"));
        await Should.ThrowAsync<NonSortablePackageVersionException>(() => AssignAsync(feedId, "{counter}"));

        (await RowCountAsync("SELECT COALESCE(MAX(value), 0) FROM package_version_counter")).ShouldBe(1, "a rejected assignment must not take a number");
        (await AssignAsync(feedId, "1.0.{counter}")).ShouldBe("1.0.2");
    }

    [Fact]
    public async Task Counter_ANumberTakenInsideACallersTransactionThatRollsBack_IsNeverGivenOutAgain()
    {
        var feedId = await SeedFeedAsync();

        // The caller takes 1.0.1 inside its unit of work and then fails, so its transaction rolls back. The number was committed on its own: a gap, never a duplicate.
        await Run<IPackageVersionAssigner, SquidDbContext>(async (assigner, db) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync().ConfigureAwait(false);

            (await assigner.AssignAsync("1.0.{counter}", Upload(feedId), CancellationToken.None)).ShouldBe("1.0.1");

            await transaction.RollbackAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        (await AssignAsync(feedId, "1.0.{counter}")).ShouldBe("1.0.2");
    }

    [Fact]
    public async Task Counter_Preview_LeavesTheCounterUntouched()
    {
        var feedId = await SeedFeedAsync();

        await Run<IPackageVersionAssigner>(async assigner =>
        {
            await assigner.PreviewAsync("1.0.{counter}", PackageVersionSampleContext.Create(feedId, Package), CancellationToken.None).ConfigureAwait(false);
            await assigner.PreviewAsync("1.0.{counter}", PackageVersionSampleContext.Create(feedId, Package), CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        (await RowCountAsync("SELECT COUNT(*) FROM package_version_counter")).ShouldBe(0);
        (await AssignAsync(feedId, "1.0.{counter}")).ShouldBe("1.0.1");
    }

    [Fact]
    public async Task Counter_RowsGoWithTheirFeed()
    {
        var feedId = await SeedFeedAsync();

        await AssignAsync(feedId, "1.0.{counter}");
        await Run<IRepository>(async repository => await repository.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM external_feed WHERE id = {feedId}").ConfigureAwait(false));

        (await RowCountAsync("SELECT COUNT(*) FROM package_version_counter")).ShouldBe(0);
    }

    // ── Rules ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rule_WithoutOne_ResolvesToTheDefault()
    {
        var feedId = await SeedFeedAsync();

        var rule = await Run<IPackageVersionRuleService, PackageVersionRuleDto>(s => s.GetEffectiveRuleAsync(1, feedId, Package, CancellationToken.None));

        rule.IsDefault.ShouldBeTrue();
        rule.VersionTemplate.ShouldBe(PackageVersioning.DefaultTemplate);
        rule.FeedId.ShouldBe(feedId);
        rule.PackageId.ShouldBe(Package);
    }

    [Fact]
    public async Task Rule_Set_IsStored_AndASecondSetReplacesItWithoutAddingARow()
    {
        var feedId = await SeedFeedAsync();

        await SetAsync(feedId, Package, "{date}.{counter:daily}");

        (await GetAsync(feedId, Package)).ShouldBeEquivalentTo(new PackageVersionRuleDto { FeedId = feedId, PackageId = Package, VersionTemplate = "{date}.{counter:daily}", IsDefault = false });

        await SetAsync(feedId, Package, "  {source.version}  ");

        (await GetAsync(feedId, Package)).VersionTemplate.ShouldBe("{source.version}");
        (await RowCountAsync("SELECT COUNT(*) FROM package_version_rule")).ShouldBe(1);
        (await GetAsync(feedId, "Another.Package")).IsDefault.ShouldBeTrue();
    }

    [Fact]
    public async Task Rule_Set_KeepsCreatedAuditAndMovesModifiedAudit()
    {
        var feedId = await SeedFeedAsync();

        await SetAsync(feedId, Package, "1.0.{counter}");

        var first = await ReadRuleRowAsync(feedId);

        await Task.Delay(50);
        await SetAsync(feedId, Package, "2.0.{counter}");

        var second = await ReadRuleRowAsync(feedId);

        second.CreatedDate.ShouldBe(first.CreatedDate);
        second.LastModifiedDate.ShouldBeGreaterThan(first.LastModifiedDate);
        second.VersionTemplate.ShouldBe("2.0.{counter}");
    }

    [Theory]
    [InlineData("1.0.{counter")]
    [InlineData("1.0.{nope}")]
    [InlineData("{counter}")]
    [InlineData("1.0.{counter:weekly}")]
    [InlineData("{source.version:setup.py}")]
    [InlineData("")]
    public async Task Rule_Set_WithATemplateThatCannotGiveAVersion_IsRejectedAndNothingIsStored(string template)
    {
        var feedId = await SeedFeedAsync();

        await Should.ThrowAsync<PackageVersionTemplateException>(() => SetAsync(feedId, Package, template));

        (await RowCountAsync("SELECT COUNT(*) FROM package_version_rule")).ShouldBe(0);
        (await RowCountAsync("SELECT COUNT(*) FROM package_version_counter")).ShouldBe(0, "checking a template must not take a counter number");
    }

    [Fact]
    public async Task Rule_Set_ForAFeedOfAnotherSpaceOrNoFeed_IsNotFound()
    {
        var feedId = await SeedFeedAsync(spaceId: 1);

        await Should.ThrowAsync<PackageVersionFeedNotFoundException>(() => Run<IPackageVersionRuleService>(s => s.SetRuleAsync(new SetPackageVersionRuleCommand { SpaceId = 2, FeedId = feedId, PackageId = Package, VersionTemplate = "1.0.{counter}" }, CancellationToken.None)));
        await Should.ThrowAsync<PackageVersionFeedNotFoundException>(() => SetAsync(feedId + 1000, Package, "1.0.{counter}"));
        await Should.ThrowAsync<PackageVersionFeedNotFoundException>(() => GetAsync(feedId + 1000, Package));
        (await RowCountAsync("SELECT COUNT(*) FROM package_version_rule")).ShouldBe(0);
    }

    [Fact]
    public async Task Rule_List_ReturnsOnlyThatFeedsConfiguredPackages_InOrder()
    {
        var feedA = await SeedFeedAsync();
        var feedB = await SeedFeedAsync();

        await SetAsync(feedA, "Zed", "1.0.{counter}");
        await SetAsync(feedA, "Alpha", "{input}");
        await SetAsync(feedB, "Other", "1.0.{counter}");

        var list = await Run<IPackageVersionRuleService, List<PackageVersionRuleDto>>(s => s.ListRulesAsync(1, feedA, CancellationToken.None));

        list.Select(r => (r.PackageId, r.VersionTemplate)).ShouldBe([("Alpha", "{input}"), ("Zed", "1.0.{counter}")]);
    }

    [Fact]
    public async Task Rule_Set_Concurrently_LeavesOneRow()
    {
        var feedId = await SeedFeedAsync();

        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(() => SetAsync(feedId, Package, $"{i + 1}.0.{{counter}}"))));

        (await RowCountAsync("SELECT COUNT(*) FROM package_version_rule")).ShouldBe(1);
    }

    private Task SetAsync(int feedId, string package, string template)
    {
        return Run<IPackageVersionRuleService>(s => s.SetRuleAsync(new SetPackageVersionRuleCommand { SpaceId = 1, FeedId = feedId, PackageId = package, VersionTemplate = template }, CancellationToken.None));
    }

    private Task<PackageVersionRuleDto> GetAsync(int feedId, string package)
    {
        return Run<IPackageVersionRuleService, PackageVersionRuleDto>(s => s.GetEffectiveRuleAsync(1, feedId, package, CancellationToken.None));
    }

    private Task<PackageVersionRule> ReadRuleRowAsync(int feedId)
    {
        return Run<IRepository, PackageVersionRule>(r => r.QueryNoTracking<PackageVersionRule>(x => x.FeedId == feedId).SingleAsync());
    }
}
