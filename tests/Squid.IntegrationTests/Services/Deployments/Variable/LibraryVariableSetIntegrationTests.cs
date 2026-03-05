using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.Core.Services.Deployments.Variables;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums;

namespace Squid.IntegrationTests.Services.Deployments.Variable;

[Collection("LibraryVariableSet Tests")]
public class LibraryVariableSetIntegrationTests : TestBase
{
    public LibraryVariableSetIntegrationTests()
        : base("_library_variable_set_", "squid_test_library_vs")
    {
    }

    [Fact]
    public async Task CreateLibraryVariableSet_BothTablesPopulated()
    {
        await Run<IRepository, IUnitOfWork, ILibraryVariableSetDataProvider>(async (repository, unitOfWork, libraryProvider) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync(VariableSetOwnerType.LibraryVariableSet).ConfigureAwait(false);
            var libraryVs = await builder.CreateLibraryVariableSetAsync(variableSet.Id, "Shared Secrets").ConfigureAwait(false);

            variableSet.OwnerId = libraryVs.Id;
            await repository.UpdateAsync(variableSet).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var loaded = await libraryProvider.GetByIdAsync(libraryVs.Id).ConfigureAwait(false);

            loaded.ShouldNotBeNull();
            loaded.Name.ShouldBe("Shared Secrets");
            loaded.VariableSetId.ShouldBe(variableSet.Id);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task GetByIds_ReturnsMatchingOnly()
    {
        await Run<IRepository, IUnitOfWork, ILibraryVariableSetDataProvider>(async (repository, unitOfWork, libraryProvider) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var vs1 = await builder.CreateVariableSetAsync(VariableSetOwnerType.LibraryVariableSet).ConfigureAwait(false);
            var lvs1 = await builder.CreateLibraryVariableSetAsync(vs1.Id, "Set1").ConfigureAwait(false);

            var vs2 = await builder.CreateVariableSetAsync(VariableSetOwnerType.LibraryVariableSet).ConfigureAwait(false);
            var lvs2 = await builder.CreateLibraryVariableSetAsync(vs2.Id, "Set2").ConfigureAwait(false);

            var vs3 = await builder.CreateVariableSetAsync(VariableSetOwnerType.LibraryVariableSet).ConfigureAwait(false);
            var lvs3 = await builder.CreateLibraryVariableSetAsync(vs3.Id, "Set3").ConfigureAwait(false);

            var result = await libraryProvider.GetByIdsAsync(new List<int> { lvs1.Id, lvs3.Id }).ConfigureAwait(false);

            result.Count.ShouldBe(2);
            result.ShouldContain(x => x.Id == lvs1.Id);
            result.ShouldContain(x => x.Id == lvs3.Id);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task GetByIds_EmptyList_ReturnsEmpty()
    {
        await Run<ILibraryVariableSetDataProvider>(async libraryProvider =>
        {
            var result = await libraryProvider.GetByIdsAsync(new List<int>()).ConfigureAwait(false);

            result.ShouldBeEmpty();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task VariableResolution_ThroughLibraryVariableSet_ResolvesVariables()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService, ILibraryVariableSetDataProvider>(
            async (repository, unitOfWork, snapshotService, libraryProvider) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            // Create project with its own variable set
            var projectVs = await builder.CreateVariableSetAsync(VariableSetOwnerType.Project).ConfigureAwait(false);
            await builder.CreateVariableAsync(projectVs.Id, "ProjectVar", "ProjectValue").ConfigureAwait(false);

            // Create library variable set with variables
            var libraryVs = await builder.CreateVariableSetAsync(VariableSetOwnerType.LibraryVariableSet).ConfigureAwait(false);
            await builder.CreateVariableAsync(libraryVs.Id, "LibraryVar", "LibraryValue").ConfigureAwait(false);
            var lvs = await builder.CreateLibraryVariableSetAsync(libraryVs.Id, "SharedVars").ConfigureAwait(false);

            libraryVs.OwnerId = lvs.Id;
            await repository.UpdateAsync(libraryVs).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            // Resolve: LibraryVariableSet.Id → VariableSetId
            var resolved = await libraryProvider.GetByIdsAsync(new List<int> { lvs.Id }).ConfigureAwait(false);
            var variableSetIds = new List<int> { projectVs.Id };
            variableSetIds.AddRange(resolved.Select(r => r.VariableSetId));

            // Snapshot both sets
            var snapshot = await snapshotService.SnapshotVariableSetFromIdsAsync(variableSetIds).ConfigureAwait(false);

            snapshot.Data.Variables.Count.ShouldBe(2);
            snapshot.Data.Variables.ShouldContain(v => v.Name == "ProjectVar" && v.Value == "ProjectValue");
            snapshot.Data.Variables.ShouldContain(v => v.Name == "LibraryVar" && v.Value == "LibraryValue");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task VariableResolution_MultipleLibrarySets_MergesAll()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService, ILibraryVariableSetDataProvider>(
            async (repository, unitOfWork, snapshotService, libraryProvider) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var projectVs = await builder.CreateVariableSetAsync(VariableSetOwnerType.Project).ConfigureAwait(false);
            await builder.CreateVariableAsync(projectVs.Id, "ProjectVar", "PV").ConfigureAwait(false);

            var lib1Vs = await builder.CreateVariableSetAsync(VariableSetOwnerType.LibraryVariableSet).ConfigureAwait(false);
            await builder.CreateVariableAsync(lib1Vs.Id, "DbHost", "db.prod").ConfigureAwait(false);
            var lvs1 = await builder.CreateLibraryVariableSetAsync(lib1Vs.Id, "Database").ConfigureAwait(false);

            var lib2Vs = await builder.CreateVariableSetAsync(VariableSetOwnerType.LibraryVariableSet).ConfigureAwait(false);
            await builder.CreateVariableAsync(lib2Vs.Id, "ApiKey", "secret123").ConfigureAwait(false);
            var lvs2 = await builder.CreateLibraryVariableSetAsync(lib2Vs.Id, "ApiKeys").ConfigureAwait(false);

            var resolved = await libraryProvider.GetByIdsAsync(new List<int> { lvs1.Id, lvs2.Id }).ConfigureAwait(false);
            var variableSetIds = new List<int> { projectVs.Id };
            variableSetIds.AddRange(resolved.Select(r => r.VariableSetId));

            var snapshot = await snapshotService.SnapshotVariableSetFromIdsAsync(variableSetIds).ConfigureAwait(false);

            snapshot.Data.Variables.Count.ShouldBe(3);
            snapshot.Data.Variables.ShouldContain(v => v.Name == "ProjectVar");
            snapshot.Data.Variables.ShouldContain(v => v.Name == "DbHost");
            snapshot.Data.Variables.ShouldContain(v => v.Name == "ApiKey");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SnapshotFromRelease_WithLibraryVariableSets_ResolvesAll()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService>(
            async (repository, unitOfWork, snapshotService) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            // Create project variable set
            var projectVs = await builder.CreateVariableSetAsync(VariableSetOwnerType.Project).ConfigureAwait(false);
            await builder.CreateVariableAsync(projectVs.Id, "ProjectVar", "ProjVal").ConfigureAwait(false);

            // Create library variable set
            var libraryVs = await builder.CreateVariableSetAsync(VariableSetOwnerType.LibraryVariableSet).ConfigureAwait(false);
            await builder.CreateVariableAsync(libraryVs.Id, "LibVar", "LibVal").ConfigureAwait(false);
            var lvs = await builder.CreateLibraryVariableSetAsync(libraryVs.Id, "MyLib").ConfigureAwait(false);

            libraryVs.OwnerId = lvs.Id;
            await repository.UpdateAsync(libraryVs).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            // Create project with included library variable set
            var project = await builder.CreateProjectAsync(projectVs.Id).ConfigureAwait(false);
            await builder.UpdateVariableSetOwnerAsync(projectVs, project.Id).ConfigureAwait(false);

            project.IncludedLibraryVariableSetIds = $"[{lvs.Id}]";
            await repository.UpdateAsync(project).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            // Create release
            var channel = await builder.CreateChannelAsync(project.Id).ConfigureAwait(false);
            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            // Snapshot from release
            var snapshot = await snapshotService.SnapshotVariableSetFromReleaseAsync(release).ConfigureAwait(false);

            snapshot.ShouldNotBeNull();
            snapshot.Data.Variables.ShouldContain(v => v.Name == "ProjectVar" && v.Value == "ProjVal");
            snapshot.Data.Variables.ShouldContain(v => v.Name == "LibVar" && v.Value == "LibVal");
        }).ConfigureAwait(false);
    }
}
