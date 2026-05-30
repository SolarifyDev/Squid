using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Machines;
using Squid.Core.Services.Machines.Cleanup;
using Squid.IntegrationTests.Base;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.IntegrationTests.Services.Machines;

/// <summary>
/// Integration coverage for machine-policy cleanup enforcement against a real
/// Postgres DB. The per-policy <c>DeleteMachinesBehavior</c> is the sole control:
/// an eligible long-unavailable target under a <c>DeleteUnavailableMachines</c>
/// policy is removed via the real <c>IMachineService</c> path; every other case
/// (DoNotDelete, within-grace, unknown go-bad instant, healthy) is kept. Also pins
/// the <c>UnavailableSince</c> column round-trip.
/// </summary>
public class MachineCleanupServiceTests : TestBase
{
    private static readonly JsonSerializerOptions PolicyJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public MachineCleanupServiceTests() : base("MachineCleanup", "squid_it_machine_cleanup")
    {
    }

    [Fact]
    public async Task EligibleMachine_IsDeleted()
    {
        var machineId = await SeedMachineAsync(DeleteMachinesBehavior.DeleteUnavailableMachines, afterSeconds: 86400,
            status: MachineHealthStatus.Unavailable, unavailableSince: DateTimeOffset.UtcNow.AddDays(-7)).ConfigureAwait(false);

        var outcome = await EnforceAsync().ConfigureAwait(false);

        outcome.Eligible.ShouldBe(1);
        outcome.Deleted.ShouldBe(1);
        (await MachineExistsAsync(machineId).ConfigureAwait(false)).ShouldBeFalse(
            customMessage: "A DeleteUnavailableMachines policy must delete a target unavailable past its grace period, via the real delete path.");
    }

    [Fact]
    public async Task DoNotDeletePolicy_KeepsMachine()
    {
        var machineId = await SeedMachineAsync(DeleteMachinesBehavior.DoNotDelete, afterSeconds: 86400,
            status: MachineHealthStatus.Unavailable, unavailableSince: DateTimeOffset.UtcNow.AddDays(-30)).ConfigureAwait(false);

        var outcome = await EnforceAsync().ConfigureAwait(false);

        outcome.Eligible.ShouldBe(0);
        (await MachineExistsAsync(machineId).ConfigureAwait(false)).ShouldBeTrue(
            customMessage: "The default DoNotDelete policy must never delete a machine — it is the opt-in gate.");
    }

    [Fact]
    public async Task WithinGracePeriod_KeepsMachine()
    {
        var machineId = await SeedMachineAsync(DeleteMachinesBehavior.DeleteUnavailableMachines, afterSeconds: 86400,
            status: MachineHealthStatus.Unavailable, unavailableSince: DateTimeOffset.UtcNow.AddHours(-1)).ConfigureAwait(false);

        var outcome = await EnforceAsync().ConfigureAwait(false);

        outcome.Eligible.ShouldBe(0);
        (await MachineExistsAsync(machineId).ConfigureAwait(false)).ShouldBeTrue();
    }

    [Fact]
    public async Task UnknownGoBadInstant_KeepsMachine()
    {
        var machineId = await SeedMachineAsync(DeleteMachinesBehavior.DeleteUnavailableMachines, afterSeconds: 86400,
            status: MachineHealthStatus.Unavailable, unavailableSince: null).ConfigureAwait(false);

        var outcome = await EnforceAsync().ConfigureAwait(false);

        outcome.Eligible.ShouldBe(0);
        (await MachineExistsAsync(machineId).ConfigureAwait(false)).ShouldBeTrue(
            customMessage: "A null UnavailableSince means unknown downtime duration — never eligible.");
    }

    [Fact]
    public async Task HealthyMachine_KeepsMachine()
    {
        var machineId = await SeedMachineAsync(DeleteMachinesBehavior.DeleteUnavailableMachines, afterSeconds: 86400,
            status: MachineHealthStatus.Healthy, unavailableSince: null).ConfigureAwait(false);

        var outcome = await EnforceAsync().ConfigureAwait(false);

        outcome.Eligible.ShouldBe(0);
        (await MachineExistsAsync(machineId).ConfigureAwait(false)).ShouldBeTrue();
    }

    [Fact]
    public async Task UnavailableSince_RoundTripsThroughDb()
    {
        var since = new DateTimeOffset(2026, 5, 20, 8, 0, 0, TimeSpan.Zero);

        var machineId = await SeedMachineAsync(DeleteMachinesBehavior.DoNotDelete, afterSeconds: 86400,
            status: MachineHealthStatus.Unavailable, unavailableSince: since).ConfigureAwait(false);

        var loaded = await Run<IMachineDataProvider, Machine>(p => p.GetMachinesByIdAsync(machineId, CancellationToken.None)).ConfigureAwait(false);

        loaded.ShouldNotBeNull();
        loaded.UnavailableSince.ShouldBe(since);
    }

    // ── helpers ──

    private Task<MachineCleanupOutcome> EnforceAsync()
        => Run<IMachineCleanupService, MachineCleanupOutcome>(svc => svc.EnforceCleanupAsync(CancellationToken.None));

    private async Task<int> SeedMachineAsync(DeleteMachinesBehavior behavior, int afterSeconds, MachineHealthStatus status, DateTimeOffset? unavailableSince)
    {
        var machineId = 0;

        await Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var cleanupJson = JsonSerializer.Serialize(new MachineCleanupPolicyDto
            {
                DeleteMachinesBehavior = behavior,
                DeleteMachinesAfterSeconds = afterSeconds
            }, PolicyJson);

            var policy = new MachinePolicy
            {
                SpaceId = 1,
                Name = $"cleanup-policy-{Guid.NewGuid():N}",
                Description = "integration test policy",
                IsDefault = false,
                MachineCleanupPolicy = cleanupJson,
                MachineHealthCheckPolicy = "{}",
                MachineConnectivityPolicy = "{}",
                MachineUpdatePolicy = "{}",
                MachineRpcCallRetryPolicy = "{}",
                PollingRequestQueueTimeout = "00:10:00",
                ConnectionRetrySleepInterval = "00:00:01",
                ConnectionRetryTimeLimit = "00:05:00",
                ConnectionConnectTimeout = "00:01:00"
            };
            await repo.InsertAsync(policy, CancellationToken.None).ConfigureAwait(false);
            await uow.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            var machine = new Machine
            {
                Name = $"cleanup-target-{Guid.NewGuid():N}",
                IsDisabled = false,
                Roles = "[]",
                EnvironmentIds = "[]",
                MachinePolicyId = policy.Id,
                Endpoint = "{\"CommunicationStyle\":\"KubernetesApi\"}",
                SpaceId = 1,
                Slug = $"cleanup-target-{Guid.NewGuid():N}",
                HealthStatus = status,
                HealthLastChecked = DateTimeOffset.UtcNow,
                UnavailableSince = unavailableSince
            };
            await repo.InsertAsync(machine, CancellationToken.None).ConfigureAwait(false);
            await uow.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            machineId = machine.Id;
        }).ConfigureAwait(false);

        return machineId;
    }

    private Task<bool> MachineExistsAsync(int machineId)
        => Run<IMachineDataProvider, bool>(async p => await p.GetMachinesByIdAsync(machineId, CancellationToken.None).ConfigureAwait(false) != null);
}
