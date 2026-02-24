using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Db;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums;

namespace Squid.IntegrationTests.Deployments.Snapshots;

public class IntegrationVariableSetSnapshot : SnapshotFixtureBase
{
    [Fact]
    public async Task SnapshotVariableSetFromIdsAsync_CreatesCompressedSnapshot()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService>(async (repository, unitOfWork, snapshotService) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync();
            await builder.CreateVariablesAsync(variableSet.Id,
                ("AppName", "Squid", VariableType.String, false),
                ("Port", "8080", VariableType.String, false));

            var snapshot = await snapshotService.SnapshotVariableSetFromIdsAsync(new List<int> { variableSet.Id });

            snapshot.ShouldNotBeNull();
            snapshot.Id.ShouldBeGreaterThan(0);
            snapshot.Data.ShouldNotBeNull();
            snapshot.Data.Variables.Count.ShouldBe(2);
            snapshot.Data.Variables.ShouldContain(v => v.Name == "AppName" && v.Value == "Squid");
            snapshot.Data.Variables.ShouldContain(v => v.Name == "Port" && v.Value == "8080");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task LoadVariableSetSnapshotAsync_RoundTrip_DataMatchesOriginal()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService>(async (repository, unitOfWork, snapshotService) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync();
            await builder.CreateVariableAsync(variableSet.Id, "RoundTrip", "Value1");

            var created = await snapshotService.SnapshotVariableSetFromIdsAsync(new List<int> { variableSet.Id });
            var loaded = await snapshotService.LoadVariableSetSnapshotAsync(created.Id);

            loaded.ShouldNotBeNull();
            loaded.Id.ShouldBe(created.Id);
            loaded.Data.Variables.Count.ShouldBe(created.Data.Variables.Count);
            loaded.Data.Variables[0].Name.ShouldBe(created.Data.Variables[0].Name);
            loaded.Data.Variables[0].Value.ShouldBe(created.Data.Variables[0].Value);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SnapshotVariableSetFromIdsAsync_SensitiveVariable_PreservesFlag()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService>(async (repository, unitOfWork, snapshotService) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync();
            await builder.CreateVariableAsync(variableSet.Id, "Secret", "s3cret!", isSensitive: true);
            await builder.CreateVariableAsync(variableSet.Id, "Public", "hello", isSensitive: false);

            var snapshot = await snapshotService.SnapshotVariableSetFromIdsAsync(new List<int> { variableSet.Id });

            var sensitiveVar = snapshot.Data.Variables.First(v => v.Name == "Secret");
            sensitiveVar.IsSensitive.ShouldBeTrue();

            var publicVar = snapshot.Data.Variables.First(v => v.Name == "Public");
            publicVar.IsSensitive.ShouldBeFalse();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SnapshotVariableSetFromIdsAsync_MultipleVariableSets_MergesAll()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService>(async (repository, unitOfWork, snapshotService) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var set1 = await builder.CreateVariableSetAsync();
            await builder.CreateVariableAsync(set1.Id, "VarFromSet1", "Value1");

            var set2 = await builder.CreateVariableSetAsync();
            await builder.CreateVariableAsync(set2.Id, "VarFromSet2", "Value2");

            var set3 = await builder.CreateVariableSetAsync();
            await builder.CreateVariableAsync(set3.Id, "VarFromSet3", "Value3");

            var snapshot = await snapshotService.SnapshotVariableSetFromIdsAsync(
                new List<int> { set1.Id, set2.Id, set3.Id });

            snapshot.Data.Variables.Count.ShouldBe(3);
            snapshot.Data.Variables.ShouldContain(v => v.Name == "VarFromSet1");
            snapshot.Data.Variables.ShouldContain(v => v.Name == "VarFromSet2");
            snapshot.Data.Variables.ShouldContain(v => v.Name == "VarFromSet3");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SnapshotVariableSetFromIdsAsync_Idempotency_SameHash()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService>(async (repository, unitOfWork, snapshotService) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync();
            await builder.CreateVariableAsync(variableSet.Id, "Stable", "StableValue");

            var snapshot1 = await snapshotService.SnapshotVariableSetFromIdsAsync(new List<int> { variableSet.Id });
            var snapshot2 = await snapshotService.SnapshotVariableSetFromIdsAsync(new List<int> { variableSet.Id });

            snapshot1.Id.ShouldBe(snapshot2.Id);
            snapshot1.Data.Variables.Count.ShouldBe(snapshot2.Data.Variables.Count);
            snapshot1.Data.Variables[0].Name.ShouldBe(snapshot2.Data.Variables[0].Name);
            snapshot1.Data.Variables[0].Value.ShouldBe(snapshot2.Data.Variables[0].Value);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SnapshotVariableSetFromReleaseAsync_FollowsChain()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService>(async (repository, unitOfWork, snapshotService) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync();
            await builder.CreateVariableAsync(variableSet.Id, "ProjectVar", "ProjValue");

            var project = await builder.CreateProjectAsync(variableSet.Id);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id);

            var channel = await builder.CreateChannelAsync(project.Id);
            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0");

            var snapshot = await snapshotService.SnapshotVariableSetFromReleaseAsync(release);

            snapshot.ShouldNotBeNull();
            snapshot.Data.Variables.ShouldContain(v => v.Name == "ProjectVar" && v.Value == "ProjValue");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SnapshotVariableSetFromIdsAsync_Isolation_ModifySourceAfterSnapshot_SnapshotUnchanged()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService>(async (repository, unitOfWork, snapshotService) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync();
            await builder.CreateVariableAsync(variableSet.Id, "Original", "OrigValue");

            var snapshot = await snapshotService.SnapshotVariableSetFromIdsAsync(new List<int> { variableSet.Id });
            var snapshotId = snapshot.Id;

            // Modify source: add new variable, change existing
            await builder.CreateVariableAsync(variableSet.Id, "NewVar", "NewValue");

            // Reload snapshot — must still reflect original data
            var reloaded = await snapshotService.LoadVariableSetSnapshotAsync(snapshotId);

            reloaded.Data.Variables.Count.ShouldBe(1);
            reloaded.Data.Variables[0].Name.ShouldBe("Original");
            reloaded.Data.Variables[0].Value.ShouldBe("OrigValue");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SnapshotVariableSetFromIdsAsync_DeduplicationBreaksOnChange_NewSnapshotCreated()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService>(async (repository, unitOfWork, snapshotService) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync();
            await builder.CreateVariableAsync(variableSet.Id, "V1", "Value1");

            var snapshotV1 = await snapshotService.SnapshotVariableSetFromIdsAsync(new List<int> { variableSet.Id });

            // Add a variable → content changes → new hash
            await builder.CreateVariableAsync(variableSet.Id, "V2", "Value2");

            var snapshotV2 = await snapshotService.SnapshotVariableSetFromIdsAsync(new List<int> { variableSet.Id });

            snapshotV2.Id.ShouldNotBe(snapshotV1.Id);
            snapshotV2.Data.Variables.Count.ShouldBe(2);

            // Original snapshot unchanged
            var reloadedV1 = await snapshotService.LoadVariableSetSnapshotAsync(snapshotV1.Id);
            reloadedV1.Data.Variables.Count.ShouldBe(1);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SnapshotVariableSetFromIdsAsync_EmptyVariableSetIds_ReturnsEmptySnapshot()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService>(async (_, _, snapshotService) =>
        {
            var snapshot = await snapshotService.SnapshotVariableSetFromIdsAsync(new List<int>());

            snapshot.ShouldNotBeNull();
            snapshot.Id.ShouldBeGreaterThan(0);
            snapshot.Data.Variables.ShouldBeEmpty();
        }).ConfigureAwait(false);
    }
}
