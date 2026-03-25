using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Machines;
using Squid.Message.Constants;
using Squid.Message.Enums;

namespace Squid.IntegrationTests.Services.Audit;

public class AuditColumnPersistenceTests : TestBase
{
    public AuditColumnPersistenceTests()
        : base("AuditColumn", "squid_it_audit_column")
    {
    }

    [Fact]
    public async Task Insert_AuditableEntity_PopulatesAllFourColumns()
    {
        var channelId = 0;

        await Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var channel = new Channel
            {
                Name = "audit-test",
                ProjectId = 0,
                SpaceId = 1,
                Slug = "audit-test",
                IsDefault = false
            };

            await repo.InsertAsync(channel);
            await uow.SaveChangesAsync();

            channelId = channel.Id;
        }).ConfigureAwait(false);

        await Run<IRepository>(async repo =>
        {
            var channel = await repo.GetByIdAsync<Channel>(channelId);

            channel.ShouldNotBeNull();
            channel.CreatedDate.ShouldNotBe(default);
            channel.CreatedBy.ShouldBe(CurrentUsers.InternalUser.Id);
            channel.LastModifiedDate.ShouldNotBe(default);
            channel.LastModifiedBy.ShouldBe(CurrentUsers.InternalUser.Id);
            channel.CreatedDate.ShouldBe(channel.LastModifiedDate);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Update_AuditableEntity_OnlyChangesLastModifiedFields()
    {
        var channelId = 0;
        DateTimeOffset originalCreatedDate = default;

        await Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var channel = new Channel
            {
                Name = "audit-update-test",
                ProjectId = 0,
                SpaceId = 1,
                Slug = "audit-update",
                IsDefault = false
            };

            await repo.InsertAsync(channel);
            await uow.SaveChangesAsync();

            channelId = channel.Id;
            originalCreatedDate = channel.CreatedDate;
        }).ConfigureAwait(false);

        await Task.Delay(50);

        await Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var channel = await repo.GetByIdAsync<Channel>(channelId);
            channel.Name = "updated-name";
            await repo.UpdateAsync(channel);
            await uow.SaveChangesAsync();
        }).ConfigureAwait(false);

        await Run<IRepository>(async repo =>
        {
            var channel = await repo.GetByIdAsync<Channel>(channelId);

            channel.ShouldNotBeNull();

            // PostgreSQL timestamptz has microsecond precision (6 digits),
            // while DateTimeOffset.UtcNow has tick precision (7 digits).
            // Compare with a tolerance to account for the truncation on round-trip.
            var createdDelta = (channel.CreatedDate - originalCreatedDate).Duration();
            createdDelta.TotalMicroseconds.ShouldBeLessThan(1);

            channel.CreatedBy.ShouldBe(CurrentUsers.InternalUser.Id);
            channel.LastModifiedDate.ShouldBeGreaterThan(channel.CreatedDate);
            channel.LastModifiedBy.ShouldBe(CurrentUsers.InternalUser.Id);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Machine_HealthLastChecked_PersistsAsTimestamptz()
    {
        var machineId = 0;
        var healthCheckTime = DateTimeOffset.UtcNow;

        await Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var machine = new Machine
            {
                Name = "health-tz-test",
                SpaceId = 1,
                Slug = "health-tz",
                HealthStatus = MachineHealthStatus.Healthy,
                HealthLastChecked = healthCheckTime,
                Endpoint = "{}",
                Roles = "[]",
                EnvironmentIds = "[]"
            };

            await repo.InsertAsync(machine);
            await uow.SaveChangesAsync();

            machineId = machine.Id;
        }).ConfigureAwait(false);

        await Run<IRepository>(async repo =>
        {
            var machine = await repo.GetByIdAsync<Machine>(machineId);

            machine.ShouldNotBeNull();
            machine.HealthLastChecked.ShouldNotBeNull();

            var delta = (machine.HealthLastChecked.Value - healthCheckTime).Duration();
            delta.TotalSeconds.ShouldBeLessThan(1);
        }).ConfigureAwait(false);
    }
}
