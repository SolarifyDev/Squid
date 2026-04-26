using System.Linq;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Machines;
using Xunit;

namespace Squid.UnitTests.Services.Machines;

/// <summary>
/// P1-D.8 (Phase-8): pin the explicit per-space vs cross-space split on
/// <see cref="IMachineDataProvider"/>.
///
/// <para><b>The bug it closes</b>: pre-fix
/// <c>GetMachinePagingAsync(int? spaceId = null, ...)</c> silently scanned
/// every space when <paramref name="spaceId"/> was null. Any
/// <c>MachineView</c> holder calling <c>GET /machines</c> without an
/// <c>X-Space-Id</c> header would enumerate every space's machines,
/// endpoints, and Halibut thumbprints — cross-space data exfiltration
/// without explicit AdministerSystem privilege.</para>
///
/// <para><b>Fix</b>: the API is split. Per-space callers get
/// <c>GetMachinesInSpacePagingAsync(int spaceId, ...)</c> with a NON-null
/// spaceId enforced at the type level. Cross-space callers
/// (AdministerSystem dashboards, health-check sweep) call
/// <c>GetMachinesAllSpacesPagingAsync()</c> — explicitly named so the
/// intent is visible at every call site. The legacy nullable entry is
/// kept obsolete-marked for back-compat.</para>
/// </summary>
public sealed class MachineDataProviderD8Tests
{
    [Fact]
    public async Task GetMachinesInSpace_ReturnsOnlyMatchingSpace()
    {
        var (provider, _) = NewProvider();

        await SeedAsync(provider, ("agent-a", spaceId: 1), ("agent-b", spaceId: 1), ("agent-c", spaceId: 2));

        var (count, machines) = await provider.GetMachinesInSpacePagingAsync(spaceId: 1, cancellationToken: CancellationToken.None);

        count.ShouldBe(2);
        machines.Select(m => m.Name).OrderBy(n => n).ShouldBe(new[] { "agent-a", "agent-b" });
    }

    [Fact]
    public async Task GetMachinesInSpace_NoMatchingSpace_ReturnsEmpty()
    {
        var (provider, _) = NewProvider();

        await SeedAsync(provider, ("agent-x", spaceId: 99));

        var (count, machines) = await provider.GetMachinesInSpacePagingAsync(spaceId: 1, cancellationToken: CancellationToken.None);

        count.ShouldBe(0);
        machines.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetMachinesAllSpaces_ReturnsEveryMachineRegardlessOfSpace()
    {
        // The legitimate cross-space caller path (AdministerSystem /
        // health-check sweep). Returns all machines.
        var (provider, _) = NewProvider();

        await SeedAsync(provider, ("agent-a", 1), ("agent-b", 2), ("agent-c", 3));

        var (count, machines) = await provider.GetMachinesAllSpacesPagingAsync(cancellationToken: CancellationToken.None);

        count.ShouldBe(3);
        machines.Select(m => m.SpaceId).OrderBy(s => s).ShouldBe(new[] { 1, 2, 3 });
    }

    [Fact]
    public async Task LegacyNullEntry_StillRoutesCorrectly_ButObsoleteFlagsTheCallSite()
    {
        // Back-compat: the obsolete-marked legacy method still works —
        // null → cross-space, non-null → per-space. The point of the
        // [Obsolete] attribute is to surface call-site warnings so
        // engineers migrate away.
        var (provider, _) = NewProvider();

        await SeedAsync(provider, ("a", 1), ("b", 2));

#pragma warning disable CS0618   // intentional: testing the back-compat method
        var (countNull, machinesAll) = await provider.GetMachinePagingAsync(spaceId: null, cancellationToken: CancellationToken.None);
        var (countOne, machinesOne) = await provider.GetMachinePagingAsync(spaceId: 1, cancellationToken: CancellationToken.None);
#pragma warning restore CS0618

        countNull.ShouldBe(2, customMessage: "legacy null spaceId still cross-space scans (the back-compat behaviour the obsolete attribute warns about).");
        countOne.ShouldBe(1);
        machinesOne[0].SpaceId.ShouldBe(1);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (IMachineDataProvider Provider, SquidDbContext Db) NewProvider()
    {
        var options = new DbContextOptionsBuilder<SquidDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        // SquidDbContext implements both DbContext AND IUnitOfWork — single
        // instance plays both roles.
        var db = new SquidDbContext(options);
        var repo = new EfRepository(db);
        return (new MachineDataProvider(unitOfWork: db, repository: repo), db);
    }

    private static async Task SeedAsync(IMachineDataProvider provider, params (string Name, int SpaceId)[] machines)
    {
        foreach (var (name, spaceId) in machines)
        {
            await provider.AddMachineAsync(new Machine
            {
                Name = name,
                SpaceId = spaceId,
                Endpoint = "{}",
                Roles = "[]"
            }, forceSave: true);
        }
    }
}
