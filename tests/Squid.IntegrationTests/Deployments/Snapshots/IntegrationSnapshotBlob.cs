using System.Diagnostics;
using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Services.Common;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Snapshots;

namespace Squid.IntegrationTests.Deployments.Snapshots;

public class IntegrationSnapshotBlob : SnapshotFixtureBase
{
    [Fact]
    public async Task ProcessSnapshot_BlobMetadata_HashAndSizeAndCompressionCorrect()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService, IDeploymentSnapshotDataProvider>(
            async (repository, unitOfWork, snapshotService, snapshotDataProvider) =>
            {
                var builder = new TestDataBuilder(repository, unitOfWork);

                var process = await builder.CreateDeploymentProcessAsync();
                var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Blob Step");
                var action = await builder.CreateDeploymentActionAsync(step.Id, 1, "Blob Action");
                await builder.CreateActionPropertiesAsync(action.Id, ("Key", "Value"));

                var snapshotDto = await snapshotService.SnapshotProcessFromIdAsync(process.Id);
                var entity = await snapshotDataProvider.GetDeploymentProcessSnapshotByIdAsync(snapshotDto.Id);

                entity.ShouldNotBeNull();
                entity.CompressionType.ShouldBe("GZIP");
                entity.ContentHash.ShouldNotBeNullOrEmpty();
                entity.ContentHash.Length.ShouldBe(64); // SHA-256 hex = 64 chars
                entity.UncompressedSize.ShouldBeGreaterThan(0);
                entity.SnapshotData.Length.ShouldBeGreaterThan(0);
                entity.SnapshotData.Length.ShouldBeLessThan(entity.UncompressedSize); // GZIP should compress

                // Verify hash matches decompressed content
                var decompressed = UtilService.DecompressFromGzip<DeploymentProcessSnapshotDataDto>(entity.SnapshotData);
                var json = JsonSerializer.Serialize(decompressed);
                var expectedHash = UtilService.ComputeSha256Hash(json);
                var expectedSize = Encoding.UTF8.GetByteCount(json);

                entity.ContentHash.ShouldBe(expectedHash);
                entity.UncompressedSize.ShouldBe(expectedSize);
            }).ConfigureAwait(false);
    }

    [Fact]
    public async Task VariableSnapshot_BlobMetadata_HashAndSizeAndCompressionCorrect()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService, IDeploymentSnapshotDataProvider>(
            async (repository, unitOfWork, snapshotService, snapshotDataProvider) =>
            {
                var builder = new TestDataBuilder(repository, unitOfWork);

                var variableSet = await builder.CreateVariableSetAsync();
                await builder.CreateVariablesAsync(variableSet.Id,
                    ("Var1", "Val1", VariableType.String, false),
                    ("Secret", "s3cret", VariableType.String, true));

                var snapshotDto = await snapshotService.SnapshotVariableSetFromIdsAsync(new List<int> { variableSet.Id });
                var entity = await snapshotDataProvider.GetVariableSetSnapshotByIdAsync(snapshotDto.Id);

                entity.ShouldNotBeNull();
                entity.CompressionType.ShouldBe("GZIP");
                entity.ContentHash.Length.ShouldBe(64);
                entity.UncompressedSize.ShouldBeGreaterThan(0);
                entity.SnapshotData.Length.ShouldBeGreaterThan(0);

                var decompressed = UtilService.DecompressFromGzip<VariableSetSnapshotDataDto>(entity.SnapshotData);
                var json = JsonSerializer.Serialize(decompressed);

                entity.ContentHash.ShouldBe(UtilService.ComputeSha256Hash(json));
                entity.UncompressedSize.ShouldBe(Encoding.UTF8.GetByteCount(json));
            }).ConfigureAwait(false);
    }

    [Fact]
    public async Task ProcessSnapshot_Dedup_ExistingLookupHitReturnsExistingEntity()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService, IDeploymentSnapshotDataProvider>(
            async (repository, unitOfWork, snapshotService, snapshotDataProvider) =>
            {
                var builder = new TestDataBuilder(repository, unitOfWork);

                var process = await builder.CreateDeploymentProcessAsync();
                await builder.CreateDeploymentStepAsync(process.Id, 1, "Dedup Step");

                var first = await snapshotService.SnapshotProcessFromIdAsync(process.Id);
                var second = await snapshotService.SnapshotProcessFromIdAsync(process.Id);

                // Same ID = dedup worked
                first.Id.ShouldBe(second.Id);

                // Verify only one row exists for this process + hash combination
                var entity = await snapshotDataProvider.GetDeploymentProcessSnapshotByIdAsync(first.Id);
                var lookup = await snapshotDataProvider.GetExistingDeploymentSnapshotAsync(
                    process.Id, entity.ContentHash);

                lookup.ShouldNotBeNull();
                lookup.Id.ShouldBe(first.Id);
            }).ConfigureAwait(false);
    }

    [Fact]
    public async Task VariableSnapshot_Dedup_ExistingLookupHitReturnsExistingEntity()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService, IDeploymentSnapshotDataProvider>(
            async (repository, unitOfWork, snapshotService, snapshotDataProvider) =>
            {
                var builder = new TestDataBuilder(repository, unitOfWork);

                var variableSet = await builder.CreateVariableSetAsync();
                await builder.CreateVariableAsync(variableSet.Id, "DedupVar", "DedupVal");

                var first = await snapshotService.SnapshotVariableSetFromIdsAsync(new List<int> { variableSet.Id });
                var second = await snapshotService.SnapshotVariableSetFromIdsAsync(new List<int> { variableSet.Id });

                first.Id.ShouldBe(second.Id);

                var entity = await snapshotDataProvider.GetVariableSetSnapshotByIdAsync(first.Id);
                var lookup = await snapshotDataProvider.GetExistingVariableSetSnapshotAsync(entity.ContentHash);

                lookup.ShouldNotBeNull();
                lookup.Id.ShouldBe(first.Id);
            }).ConfigureAwait(false);
    }

    [Fact]
    public async Task ProcessSnapshot_PagingQuery_ExcludesBlobData()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService, IDeploymentSnapshotDataProvider>(
            async (repository, unitOfWork, snapshotService, snapshotDataProvider) =>
            {
                var builder = new TestDataBuilder(repository, unitOfWork);

                var process = await builder.CreateDeploymentProcessAsync();
                var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Paging Step");
                await builder.CreateDeploymentActionAsync(step.Id, 1, "Paging Action");

                await snapshotService.SnapshotProcessFromIdAsync(process.Id);

                // Add a second step to create a different snapshot
                await builder.CreateDeploymentStepAsync(process.Id, 2, "Paging Step 2");
                await snapshotService.SnapshotProcessFromIdAsync(process.Id);

                var (count, results) = await snapshotDataProvider.GetDeploymentProcessSnapshotPagingAsync(
                    originalProcessId: process.Id);

                count.ShouldBeGreaterThanOrEqualTo(2);
                results.Count.ShouldBeGreaterThanOrEqualTo(2);

                // Paging results should have metadata but no blob
                foreach (var result in results)
                {
                    result.Id.ShouldBeGreaterThan(0);
                    result.OriginalProcessId.ShouldBe(process.Id);
                    result.ContentHash.ShouldNotBeNullOrEmpty();
                    result.CompressionType.ShouldBe("GZIP");
                    result.UncompressedSize.ShouldBeGreaterThan(0);
                    result.SnapshotData.ShouldBeNull();
                }
            }).ConfigureAwait(false);
    }

    [Fact]
    public async Task ProcessSnapshot_LargeProcess_CompressesEfficiently()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService, IDeploymentSnapshotDataProvider>(
            async (repository, unitOfWork, snapshotService, snapshotDataProvider) =>
            {
                var builder = new TestDataBuilder(repository, unitOfWork);

                var process = await builder.CreateDeploymentProcessAsync();

                // Create 20 steps x 5 actions each with properties
                for (var s = 1; s <= 20; s++)
                {
                    var step = await builder.CreateDeploymentStepAsync(process.Id, s, $"Step {s}");
                    await builder.CreateStepPropertiesAsync(step.Id,
                        ("Squid.Action.TargetRoles", $"role-{s}"),
                        ($"CustomProp{s}", new string('x', 200)));

                    for (var a = 1; a <= 5; a++)
                    {
                        var action = await builder.CreateDeploymentActionAsync(step.Id, a, $"Action {s}.{a}");
                        await builder.CreateActionPropertiesAsync(action.Id,
                            ("Octopus.Action.Script.ScriptBody", $"echo step{s} action{a}\n{new string('#', 500)}"),
                            ($"Config{s}{a}", new string('y', 300)));
                        await builder.CreateActionEnvironmentsAsync(action.Id, s * 10 + a);
                        await builder.CreateActionMachineRolesAsync(action.Id, $"role-{s}-{a}");
                    }
                }

                var sw = Stopwatch.StartNew();
                var snapshot = await snapshotService.SnapshotProcessFromIdAsync(process.Id);
                sw.Stop();

                var entity = await snapshotDataProvider.GetDeploymentProcessSnapshotByIdAsync(snapshot.Id);

                // Verify data integrity
                snapshot.Data.StepSnapshots.Count.ShouldBe(20);
                snapshot.Data.StepSnapshots.All(s => s.ActionSnapshots.Count == 5).ShouldBeTrue();

                // Verify compression ratio (GZIP should compress repetitive JSON well)
                var compressionRatio = (double)entity.SnapshotData.Length / entity.UncompressedSize;
                compressionRatio.ShouldBeLessThan(0.5); // at least 50% compression

                // Verify round-trip of large snapshot
                var loaded = await snapshotService.LoadProcessSnapshotAsync(snapshot.Id);
                loaded.Data.StepSnapshots.Count.ShouldBe(20);
                loaded.Data.StepSnapshots[19].ActionSnapshots[4].Name.ShouldBe("Action 20.5");
            }).ConfigureAwait(false);
    }

    [Fact]
    public async Task VariableSnapshot_LargeVariableSet_CompressesEfficiently()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService, IDeploymentSnapshotDataProvider>(
            async (repository, unitOfWork, snapshotService, snapshotDataProvider) =>
            {
                var builder = new TestDataBuilder(repository, unitOfWork);

                var variableSet = await builder.CreateVariableSetAsync();

                // Create 200 variables with various sizes
                for (var i = 0; i < 200; i++)
                {
                    await builder.CreateVariableAsync(variableSet.Id,
                        $"Variable_{i:D3}",
                        $"Value_{new string('v', 50 + i % 100)}",
                        isSensitive: i % 10 == 0);
                }

                var snapshot = await snapshotService.SnapshotVariableSetFromIdsAsync(new List<int> { variableSet.Id });
                var entity = await snapshotDataProvider.GetVariableSetSnapshotByIdAsync(snapshot.Id);

                snapshot.Data.Variables.Count.ShouldBe(200);

                var compressionRatio = (double)entity.SnapshotData.Length / entity.UncompressedSize;
                compressionRatio.ShouldBeLessThan(0.5);

                // Round-trip
                var loaded = await snapshotService.LoadVariableSetSnapshotAsync(snapshot.Id);
                loaded.Data.Variables.Count.ShouldBe(200);
                loaded.Data.Variables.Count(v => v.IsSensitive).ShouldBe(20);
            }).ConfigureAwait(false);
    }
}
