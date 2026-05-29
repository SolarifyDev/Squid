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
using Squid.Message.Hardening;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.IntegrationTests.Services.Machines;

/// <summary>
/// Integration coverage for machine-policy cleanup enforcement against a real
/// Postgres DB. Verifies the three-mode gate end-to-end: off skips, warn is a
/// dry run (the eligible machine survives), strict actually deletes it through the
/// real <c>IMachineService</c> path. Also pins the per-policy eligibility gates
/// (DoNotDelete, within-grace, unknown go-bad instant) against the live DB, and the
/// UnavailableSince column round-trip.
///
/// <para>Env var is process-global; this class runs serially (xUnit doesn't
/// parallelise within a class) and each test restores the prior value.</para>
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
    public async Task StrictMode_EligibleMachine_IsDeleted()
    {
        var machineId = await SeedMachineAsync(DeleteMachinesBehavior.DeleteUnavailableMachines, afterSeconds: 86400,
            status: MachineHealthStatus.Unavailable, unavailableSince: DateTimeOffset.UtcNow.AddDays(-7)).ConfigureAwait(false);

        var outcome = await EnforceAsync("strict").ConfigureAwait(false);

        outcome.Deleted.ShouldBe(1);
        outcome.Eligible.ShouldBe(1);
        (await MachineExistsAsync(machineId).ConfigureAwait(false)).ShouldBeFalse(
            customMessage: "Strict mode must delete an eligible long-unavailable target via the real delete path.");
    }

    [Fact]
    public async Task WarnMode_EligibleMachine_IsReportedButNotDeleted()
    {
        var machineId = await SeedMachineAsync(DeleteMachinesBehavior.DeleteUnavailableMachines, afterSeconds: 86400,
            status: MachineHealthStatus.Unavailable, unavailableSince: DateTimeOffset.UtcNow.AddDays(-7)).ConfigureAwait(false);

        var outcome = await EnforceAsync("warn").ConfigureAwait(false);

        outcome.Eligible.ShouldBe(1);
        outcome.Deleted.ShouldBe(0,
            customMessage: "Warn mode is a dry run — it must report eligibility but delete nothing (non-breaking default).");
        (await MachineExistsAsync(machineId).ConfigureAwait(false)).ShouldBeTrue();
    }

    [Fact]
    public async Task OffMode_SkipsSweepEntirely()
    {
        var machineId = await SeedMachineAsync(DeleteMachinesBehavior.DeleteUnavailableMachines, afterSeconds: 86400,
            status: MachineHealthStatus.Unavailable, unavailableSince: DateTimeOffset.UtcNow.AddDays(-7)).ConfigureAwait(false);

        var outcome = await EnforceAsync("off").ConfigureAwait(false);

        outcome.Scanned.ShouldBe(0);
        outcome.Deleted.ShouldBe(0);
        (await MachineExistsAsync(machineId).ConfigureAwait(false)).ShouldBeTrue();
    }

    [Fact]
    public async Task DoNotDeletePolicy_StrictMode_KeepsMachine()
    {
        var machineId = await SeedMachineAsync(DeleteMachinesBehavior.DoNotDelete, afterSeconds: 86400,
            status: MachineHealthStatus.Unavailable, unavailableSince: DateTimeOffset.UtcNow.AddDays(-30)).ConfigureAwait(false);

        var outcome = await EnforceAsync("strict").ConfigureAwait(false);

        outcome.Eligible.ShouldBe(0);
        (await MachineExistsAsync(machineId).ConfigureAwait(false)).ShouldBeTrue(
            customMessage: "A DoNotDelete policy must never make a machine eligible, even in strict mode.");
    }

    [Fact]
    public async Task WithinGracePeriod_StrictMode_KeepsMachine()
    {
        var machineId = await SeedMachineAsync(DeleteMachinesBehavior.DeleteUnavailableMachines, afterSeconds: 86400,
            status: MachineHealthStatus.Unavailable, unavailableSince: DateTimeOffset.UtcNow.AddHours(-1)).ConfigureAwait(false);

        var outcome = await EnforceAsync("strict").ConfigureAwait(false);

        outcome.Eligible.ShouldBe(0);
        (await MachineExistsAsync(machineId).ConfigureAwait(false)).ShouldBeTrue();
    }

    [Fact]
    public async Task UnknownGoBadInstant_StrictMode_KeepsMachine()
    {
        var machineId = await SeedMachineAsync(DeleteMachinesBehavior.DeleteUnavailableMachines, afterSeconds: 86400,
            status: MachineHealthStatus.Unavailable, unavailableSince: null).ConfigureAwait(false);

        var outcome = await EnforceAsync("strict").ConfigureAwait(false);

        outcome.Eligible.ShouldBe(0);
        (await MachineExistsAsync(machineId).ConfigureAwait(false)).ShouldBeTrue(
            customMessage: "A null UnavailableSince means unknown downtime duration — never eligible.");
    }

    [Fact]
    public async Task HealthyMachine_StrictMode_KeepsMachine()
    {
        var machineId = await SeedMachineAsync(DeleteMachinesBehavior.DeleteUnavailableMachines, afterSeconds: 86400,
            status: MachineHealthStatus.Healthy, unavailableSince: null).ConfigureAwait(false);

        var outcome = await EnforceAsync("strict").ConfigureAwait(false);

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

    private Task<MachineCleanupOutcome> EnforceAsync(string mode)
    {
        var original = System.Environment.GetEnvironmentVariable(MachineCleanupEnforcement.EnvVar);
        System.Environment.SetEnvironmentVariable(MachineCleanupEnforcement.EnvVar, mode);

        return Run<IMachineCleanupService, MachineCleanupOutcome>(async svc =>
        {
            try
            {
                return await svc.EnforceCleanupAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                System.Environment.SetEnvironmentVariable(MachineCleanupEnforcement.EnvVar, original);
            }
        });
    }

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
