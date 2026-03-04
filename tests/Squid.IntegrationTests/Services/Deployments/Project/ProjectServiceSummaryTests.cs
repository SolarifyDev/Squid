using Squid.Core.Persistence.Db;
using Squid.Core.Services.Deployments.Project;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Requests.Deployments.Project;

namespace Squid.IntegrationTests.Services.Deployments.Project;

public class ProjectServiceSummaryTests : TestBase
{
    public ProjectServiceSummaryTests()
        : base("ProjectServiceSummary", "squid_it_project_summary")
    {
    }

    [Fact]
    public async Task GetSummaries_NoFilters_ReturnsAllGroupsAndLifecycleEnvironments()
    {
        var seed = await SeedStandardScenarioAsync().ConfigureAwait(false);

        var result = await Run<IProjectService, GetProjectSummariesResponse>(async service =>
        {
            return await service.GetProjectSummariesAsync(
                new GetProjectSummariesRequest(), CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        result.Data.ShouldNotBeNull();
        result.Data.Groups.Count.ShouldBe(1);
        result.Data.Groups[0].Projects.Count.ShouldBe(1);
        result.Data.Groups[0].EnvironmentIds.ShouldContain(seed.DevEnvId);
        result.Data.Groups[0].EnvironmentIds.ShouldContain(seed.ProdEnvId);
        result.Data.Environments.Count.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task GetSummaries_NewProjectNoDeployments_StillShowsLifecycleEnvironments()
    {
        var seed = await SeedStandardScenarioAsync().ConfigureAwait(false);

        var result = await Run<IProjectService, GetProjectSummariesResponse>(async service =>
        {
            return await service.GetProjectSummariesAsync(
                new GetProjectSummariesRequest(), CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        result.Data.Groups[0].EnvironmentIds.Count.ShouldBe(2);
        result.Data.Groups[0].EnvironmentIds.ShouldContain(seed.DevEnvId);
        result.Data.Groups[0].EnvironmentIds.ShouldContain(seed.ProdEnvId);
        result.Data.Items.Count.ShouldBe(0);
    }

    [Fact]
    public async Task GetSummaries_LifecycleNoPhases_ReturnsAllEnvironments()
    {
        int envId1 = 0, envId2 = 0, envId3 = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var lifecycle = await builder.CreateLifecycleAsync("No Phase Lifecycle").ConfigureAwait(false);
            var group = await builder.CreateProjectGroupAsync().ConfigureAwait(false);
            var env1 = await builder.CreateEnvironmentAsync("Env A").ConfigureAwait(false);
            var env2 = await builder.CreateEnvironmentAsync("Env B").ConfigureAwait(false);
            var env3 = await builder.CreateEnvironmentAsync("Env C").ConfigureAwait(false);

            envId1 = env1.Id;
            envId2 = env2.Id;
            envId3 = env3.Id;

            var varSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            await builder.CreateProjectAsync(varSet.Id, 0, group.Id, lifecycle.Id, "No Phase Project").ConfigureAwait(false);
        }).ConfigureAwait(false);

        var result = await Run<IProjectService, GetProjectSummariesResponse>(async service =>
        {
            return await service.GetProjectSummariesAsync(
                new GetProjectSummariesRequest(), CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        result.Data.Groups[0].EnvironmentIds.ShouldContain(envId1);
        result.Data.Groups[0].EnvironmentIds.ShouldContain(envId2);
        result.Data.Groups[0].EnvironmentIds.ShouldContain(envId3);
    }

    [Fact]
    public async Task GetSummaries_LifecycleWithEmptyPhase_InheritsRemainingEnvironments()
    {
        int devEnvId = 0, stagingEnvId = 0, prodEnvId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var lifecycle = await builder.CreateLifecycleAsync("Mixed Phase Lifecycle").ConfigureAwait(false);
            var group = await builder.CreateProjectGroupAsync().ConfigureAwait(false);
            var devEnv = await builder.CreateEnvironmentAsync("Development").ConfigureAwait(false);
            var stagingEnv = await builder.CreateEnvironmentAsync("Staging").ConfigureAwait(false);
            var prodEnv = await builder.CreateEnvironmentAsync("Production").ConfigureAwait(false);

            devEnvId = devEnv.Id;
            stagingEnvId = stagingEnv.Id;
            prodEnvId = prodEnv.Id;

            // Phase 1 has explicit environment (Dev)
            await builder.CreateLifecyclePhaseAsync(lifecycle.Id, devEnv.Id, "Dev Phase").ConfigureAwait(false);

            // Phase 2 is empty — should inherit Staging + Production
            await builder.CreateEmptyLifecyclePhaseAsync(lifecycle.Id, "Remaining Phase").ConfigureAwait(false);

            var varSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            await builder.CreateProjectAsync(varSet.Id, 0, group.Id, lifecycle.Id, "Mixed Project").ConfigureAwait(false);
        }).ConfigureAwait(false);

        var result = await Run<IProjectService, GetProjectSummariesResponse>(async service =>
        {
            return await service.GetProjectSummariesAsync(
                new GetProjectSummariesRequest(), CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var envIds = result.Data.Groups[0].EnvironmentIds;

        envIds.ShouldContain(devEnvId);
        envIds.ShouldContain(stagingEnvId);
        envIds.ShouldContain(prodEnvId);
    }

    [Fact]
    public async Task GetSummaries_LifecycleWithExplicitPhasesOnly_ReturnsOnlyPhaseEnvironments()
    {
        int devEnvId = 0, prodEnvId = 0, unlinkedEnvId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var lifecycle = await builder.CreateLifecycleAsync("Explicit Lifecycle").ConfigureAwait(false);
            var group = await builder.CreateProjectGroupAsync().ConfigureAwait(false);
            var devEnv = await builder.CreateEnvironmentAsync("Dev").ConfigureAwait(false);
            var prodEnv = await builder.CreateEnvironmentAsync("Prod").ConfigureAwait(false);
            var unlinkedEnv = await builder.CreateEnvironmentAsync("Unlinked").ConfigureAwait(false);

            devEnvId = devEnv.Id;
            prodEnvId = prodEnv.Id;
            unlinkedEnvId = unlinkedEnv.Id;

            await builder.CreateLifecyclePhaseAsync(lifecycle.Id, devEnv.Id, "Dev Phase").ConfigureAwait(false);
            await builder.CreateLifecyclePhaseAsync(lifecycle.Id, prodEnv.Id, "Prod Phase").ConfigureAwait(false);

            var varSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            await builder.CreateProjectAsync(varSet.Id, 0, group.Id, lifecycle.Id, "Explicit Project").ConfigureAwait(false);
        }).ConfigureAwait(false);

        var result = await Run<IProjectService, GetProjectSummariesResponse>(async service =>
        {
            return await service.GetProjectSummariesAsync(
                new GetProjectSummariesRequest(), CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var envIds = result.Data.Groups[0].EnvironmentIds;

        envIds.ShouldContain(devEnvId);
        envIds.ShouldContain(prodEnvId);
        envIds.ShouldNotContain(unlinkedEnvId);
    }

    [Fact]
    public async Task GetSummaries_FilterByProjectGroupIds_ReturnsOnlySelectedGroups()
    {
        int groupAId = 0, groupBId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var lifecycle = await builder.CreateLifecycleAsync().ConfigureAwait(false);
            var env = await builder.CreateEnvironmentAsync("Dev").ConfigureAwait(false);
            await builder.CreateLifecyclePhaseAsync(lifecycle.Id, env.Id).ConfigureAwait(false);

            var groupA = await builder.CreateProjectGroupAsync("Group A").ConfigureAwait(false);
            var groupB = await builder.CreateProjectGroupAsync("Group B").ConfigureAwait(false);

            groupAId = groupA.Id;
            groupBId = groupB.Id;

            var varSet1 = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            await builder.CreateProjectAsync(varSet1.Id, 0, groupA.Id, lifecycle.Id, "Project A").ConfigureAwait(false);

            var varSet2 = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            await builder.CreateProjectAsync(varSet2.Id, 0, groupB.Id, lifecycle.Id, "Project B").ConfigureAwait(false);
        }).ConfigureAwait(false);

        var result = await Run<IProjectService, GetProjectSummariesResponse>(async service =>
        {
            return await service.GetProjectSummariesAsync(
                new GetProjectSummariesRequest { ProjectGroupIds = new List<int> { groupAId } },
                CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        result.Data.Groups.Count.ShouldBe(1);
        result.Data.Groups[0].Name.ShouldBe("Group A");
        result.Data.Groups[0].Projects.Count.ShouldBe(1);
        result.Data.Groups[0].Projects[0].Name.ShouldBe("Project A");
    }

    [Fact]
    public async Task GetSummaries_FilterByProjectIds_ReturnsOnlySelectedProjects()
    {
        int projectAId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var lifecycle = await builder.CreateLifecycleAsync().ConfigureAwait(false);
            var env = await builder.CreateEnvironmentAsync("Dev").ConfigureAwait(false);
            await builder.CreateLifecyclePhaseAsync(lifecycle.Id, env.Id).ConfigureAwait(false);

            var group = await builder.CreateProjectGroupAsync().ConfigureAwait(false);

            var varSet1 = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var projectA = await builder.CreateProjectAsync(varSet1.Id, 0, group.Id, lifecycle.Id, "Project Alpha").ConfigureAwait(false);
            projectAId = projectA.Id;

            var varSet2 = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            await builder.CreateProjectAsync(varSet2.Id, 0, group.Id, lifecycle.Id, "Project Beta").ConfigureAwait(false);
        }).ConfigureAwait(false);

        var result = await Run<IProjectService, GetProjectSummariesResponse>(async service =>
        {
            return await service.GetProjectSummariesAsync(
                new GetProjectSummariesRequest { ProjectIds = new List<int> { projectAId } },
                CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var allProjects = result.Data.Groups.SelectMany(g => g.Projects).ToList();

        allProjects.Count.ShouldBe(1);
        allProjects[0].Name.ShouldBe("Project Alpha");
    }

    [Fact]
    public async Task GetSummaries_FilterByEnvironmentIds_ReturnsOnlySelectedEnvironments()
    {
        int devEnvId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var lifecycle = await builder.CreateLifecycleAsync().ConfigureAwait(false);
            var devEnv = await builder.CreateEnvironmentAsync("Dev").ConfigureAwait(false);
            var prodEnv = await builder.CreateEnvironmentAsync("Prod").ConfigureAwait(false);

            devEnvId = devEnv.Id;

            await builder.CreateLifecyclePhaseAsync(lifecycle.Id, devEnv.Id, "Dev Phase").ConfigureAwait(false);
            await builder.CreateLifecyclePhaseAsync(lifecycle.Id, prodEnv.Id, "Prod Phase").ConfigureAwait(false);

            var group = await builder.CreateProjectGroupAsync().ConfigureAwait(false);
            var varSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            await builder.CreateProjectAsync(varSet.Id, 0, group.Id, lifecycle.Id).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var result = await Run<IProjectService, GetProjectSummariesResponse>(async service =>
        {
            return await service.GetProjectSummariesAsync(
                new GetProjectSummariesRequest { EnvironmentIds = new List<int> { devEnvId } },
                CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        result.Data.Environments.Count.ShouldBe(1);
        result.Data.Environments[0].Id.ShouldBe(devEnvId);
        result.Data.Environments[0].Name.ShouldBe("Dev");
    }

    [Fact]
    public async Task GetSummaries_WithDeployments_ReturnsDashboardItems()
    {
        int devEnvId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var lifecycle = await builder.CreateLifecycleAsync().ConfigureAwait(false);
            var devEnv = await builder.CreateEnvironmentAsync("Dev").ConfigureAwait(false);
            devEnvId = devEnv.Id;
            await builder.CreateLifecyclePhaseAsync(lifecycle.Id, devEnv.Id).ConfigureAwait(false);

            var group = await builder.CreateProjectGroupAsync().ConfigureAwait(false);
            var varSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project = await builder.CreateProjectAsync(varSet.Id, 0, group.Id, lifecycle.Id).ConfigureAwait(false);

            var channel = await builder.CreateChannelAsync(project.Id, lifecycle.Id).ConfigureAwait(false);
            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);
            var task = await builder.CreateServerTaskAsync("Success").ConfigureAwait(false);
            await builder.CreateDeploymentAsync(project.Id, devEnv.Id, release.Id, task.Id, channel.Id).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var result = await Run<IProjectService, GetProjectSummariesResponse>(async service =>
        {
            return await service.GetProjectSummariesAsync(
                new GetProjectSummariesRequest(), CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        result.Data.Items.Count.ShouldBe(1);
        result.Data.Items[0].EnvironmentId.ShouldBe(devEnvId);
        result.Data.Items[0].ReleaseVersion.ShouldBe("1.0.0");
        result.Data.Items[0].State.ShouldBe("Success");
    }

    [Fact]
    public async Task GetSummaries_MultipleGroupsDifferentLifecycles_EachGroupGetsOwnEnvironments()
    {
        int devEnvId = 0, prodEnvId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var devEnv = await builder.CreateEnvironmentAsync("Dev").ConfigureAwait(false);
            var prodEnv = await builder.CreateEnvironmentAsync("Prod").ConfigureAwait(false);

            devEnvId = devEnv.Id;
            prodEnvId = prodEnv.Id;

            // Lifecycle A has only Dev
            var lifecycleA = await builder.CreateLifecycleAsync("Lifecycle A").ConfigureAwait(false);
            await builder.CreateLifecyclePhaseAsync(lifecycleA.Id, devEnv.Id, "Dev Phase").ConfigureAwait(false);

            // Lifecycle B has only Prod
            var lifecycleB = await builder.CreateLifecycleAsync("Lifecycle B").ConfigureAwait(false);
            await builder.CreateLifecyclePhaseAsync(lifecycleB.Id, prodEnv.Id, "Prod Phase").ConfigureAwait(false);

            var groupA = await builder.CreateProjectGroupAsync("Group A").ConfigureAwait(false);
            var groupB = await builder.CreateProjectGroupAsync("Group B").ConfigureAwait(false);

            var varSet1 = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            await builder.CreateProjectAsync(varSet1.Id, 0, groupA.Id, lifecycleA.Id, "Project A").ConfigureAwait(false);

            var varSet2 = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            await builder.CreateProjectAsync(varSet2.Id, 0, groupB.Id, lifecycleB.Id, "Project B").ConfigureAwait(false);
        }).ConfigureAwait(false);

        var result = await Run<IProjectService, GetProjectSummariesResponse>(async service =>
        {
            return await service.GetProjectSummariesAsync(
                new GetProjectSummariesRequest(), CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var groupA = result.Data.Groups.First(g => g.Name == "Group A");
        var groupB = result.Data.Groups.First(g => g.Name == "Group B");

        groupA.EnvironmentIds.ShouldContain(devEnvId);
        groupA.EnvironmentIds.ShouldNotContain(prodEnvId);

        groupB.EnvironmentIds.ShouldContain(prodEnvId);
        groupB.EnvironmentIds.ShouldNotContain(devEnvId);
    }

    private record StandardSeed(int GroupId, int ProjectId, int LifecycleId, int DevEnvId, int ProdEnvId);

    private async Task<StandardSeed> SeedStandardScenarioAsync()
    {
        var seed = new StandardSeed(0, 0, 0, 0, 0);

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var lifecycle = await builder.CreateLifecycleAsync().ConfigureAwait(false);
            var group = await builder.CreateProjectGroupAsync().ConfigureAwait(false);
            var devEnv = await builder.CreateEnvironmentAsync("Development").ConfigureAwait(false);
            var prodEnv = await builder.CreateEnvironmentAsync("Production").ConfigureAwait(false);

            await builder.CreateLifecyclePhaseAsync(lifecycle.Id, devEnv.Id, "Dev Phase").ConfigureAwait(false);
            await builder.CreateLifecyclePhaseAsync(lifecycle.Id, prodEnv.Id, "Prod Phase").ConfigureAwait(false);

            var varSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project = await builder.CreateProjectAsync(varSet.Id, 0, group.Id, lifecycle.Id).ConfigureAwait(false);

            seed = new StandardSeed(group.Id, project.Id, lifecycle.Id, devEnv.Id, prodEnv.Id);
        }).ConfigureAwait(false);

        return seed;
    }
}
