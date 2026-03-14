using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Machines;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums;

namespace Squid.IntegrationTests.Services.Machines;

public class MachineTimestampTests : TestBase
{
    public MachineTimestampTests()
        : base("MachineTimestamp", "squid_it_machine_timestamp")
    {
    }

    [Fact]
    public async Task UpdateMachine_WithHealthLastChecked_DoesNotThrow()
    {
        var machineId = await SeedMachineWithHealthCheckAsync();

        await Run<IMachineDataProvider>(async provider =>
        {
            var machine = await provider.GetMachinesByIdAsync(machineId, CancellationToken.None).ConfigureAwait(false);

            machine.Thumbprint = "NEW_THUMBPRINT_VALUE";

            await provider.UpdateMachineAsync(machine, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IMachineDataProvider>(async provider =>
        {
            var reloaded = await provider.GetMachinesByIdAsync(machineId, CancellationToken.None).ConfigureAwait(false);

            reloaded.Thumbprint.ShouldBe("NEW_THUMBPRINT_VALUE");
            reloaded.HealthLastChecked.ShouldNotBeNull();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task UpdateMachine_ViaSubscriptionIdLookup_DoesNotThrow()
    {
        var subscriptionId = Guid.NewGuid().ToString();
        await SeedMachineWithHealthCheckAsync(subscriptionId);

        await Run<IMachineDataProvider>(async provider =>
        {
            var machine = await provider.GetMachineBySubscriptionIdAsync(subscriptionId, CancellationToken.None).ConfigureAwait(false);

            machine.ShouldNotBeNull();
            machine.Thumbprint = "UPDATED_THUMBPRINT";
            machine.AgentVersion = "2.0.0";
            machine.Roles = System.Text.Json.JsonSerializer.Serialize(new[] { "web-server", "db-server" });

            await provider.UpdateMachineAsync(machine, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IMachineDataProvider>(async provider =>
        {
            var reloaded = await provider.GetMachineBySubscriptionIdAsync(subscriptionId, CancellationToken.None).ConfigureAwait(false);

            reloaded.ShouldNotBeNull();
            reloaded.Thumbprint.ShouldBe("UPDATED_THUMBPRINT");
            reloaded.AgentVersion.ShouldBe("2.0.0");
            reloaded.HealthLastChecked.ShouldNotBeNull();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task UpdateMachine_HealthLastCheckedNull_DoesNotThrow()
    {
        var machineId = await SeedMachineAsync();

        await Run<IMachineDataProvider>(async provider =>
        {
            var machine = await provider.GetMachinesByIdAsync(machineId, CancellationToken.None).ConfigureAwait(false);

            machine.HealthLastChecked.ShouldBeNull();
            machine.Name = "Updated Name";

            await provider.UpdateMachineAsync(machine, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    [Theory]
    [InlineData(MachineHealthStatus.Healthy)]
    [InlineData(MachineHealthStatus.Unhealthy)]
    [InlineData(MachineHealthStatus.Unavailable)]
    public async Task UpdateMachine_SetHealthLastCheckedThenUpdate_RoundTrips(MachineHealthStatus status)
    {
        var machineId = await SeedMachineAsync();

        await Run<IMachineDataProvider>(async provider =>
        {
            var machine = await provider.GetMachinesByIdAsync(machineId, CancellationToken.None).ConfigureAwait(false);

            machine.HealthStatus = status;
            machine.HealthLastChecked = DateTimeOffset.UtcNow;

            await provider.UpdateMachineAsync(machine, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IMachineDataProvider>(async provider =>
        {
            var machine = await provider.GetMachinesByIdAsync(machineId, CancellationToken.None).ConfigureAwait(false);

            machine.HealthLastChecked.ShouldNotBeNull();
            machine.Thumbprint = "CHANGED";

            await provider.UpdateMachineAsync(machine, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IMachineDataProvider>(async provider =>
        {
            var reloaded = await provider.GetMachinesByIdAsync(machineId, CancellationToken.None).ConfigureAwait(false);

            reloaded.Thumbprint.ShouldBe("CHANGED");
            reloaded.HealthStatus.ShouldBe(status);
            reloaded.HealthLastChecked.ShouldNotBeNull();
        }).ConfigureAwait(false);
    }

    private async Task<int> SeedMachineWithHealthCheckAsync(string subscriptionId = null)
    {
        var machineId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var machine = BuildMachine(subscriptionId);
            machine.HealthStatus = MachineHealthStatus.Healthy;
            machine.HealthLastChecked = new DateTimeOffset(2026, 3, 14, 10, 0, 0, TimeSpan.Zero);

            await repository.InsertAsync(machine).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);
            machineId = machine.Id;
        }).ConfigureAwait(false);

        return machineId;
    }

    private async Task<int> SeedMachineAsync()
    {
        var machineId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var machine = BuildMachine();

            await repository.InsertAsync(machine).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);
            machineId = machine.Id;
        }).ConfigureAwait(false);

        return machineId;
    }

    private static Machine BuildMachine(string subscriptionId = null)
    {
        return new Machine
        {
            Name = "Test Agent",
            IsDisabled = false,
            Roles = System.Text.Json.JsonSerializer.Serialize(new[] { "web-server" }),
            EnvironmentIds = System.Text.Json.JsonSerializer.Serialize(new[] { 1 }),
            SpaceId = 1,
            Endpoint = "{}",
            Json = "{}",
            DataVersion = Array.Empty<byte>(),
            ShellName = string.Empty,
            ShellVersion = string.Empty,
            LicenseHash = string.Empty,
            Slug = "test-agent",
            Thumbprint = "ORIGINAL_THUMBPRINT",
            PollingSubscriptionId = subscriptionId,
            AgentVersion = "1.0.0"
        };
    }
}
