using System.Net;
using Mediator.Net.Contracts;
using Microsoft.EntityFrameworkCore;
using Moq;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Identity;
using Squid.Message.Enums;
using Squid.Message.Requests.Mcp;
using DeploymentEntity = Squid.Core.Persistence.Entities.Deployments.Deployment;
using DeploymentEnvironmentEntity = Squid.Core.Persistence.Entities.Deployments.Environment;
using DeploymentProjectEntity = Squid.Core.Persistence.Entities.Deployments.Project;
using DeploymentProjectGroupEntity = Squid.Core.Persistence.Entities.Deployments.ProjectGroup;
using DeploymentReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;
using DeploymentSpaceEntity = Squid.Core.Persistence.Entities.Deployments.Space;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.IntegrationTests.Services.Mcp;

public class ScopedDeploymentReadTests : TestBase
{
    private const int TestSpaceId = 9101;
    private const int OtherSpaceId = 9102;
    private const int TestProjectGroupId = 9201;
    private const int OtherProjectGroupId = 9202;
    private const int ReplacementProjectGroupId = 9203;
    private const int TestProjectId = 9301;
    private const int OtherProjectId = 9302;
    private const int SecondTestProjectId = 9303;
    private const int TestEnvironmentId = 9401;
    private const int OtherEnvironmentId = 9402;

    private static readonly DateTimeOffset BaseTime = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    public ScopedDeploymentReadTests()
        : base("ScopedDeploymentRead", "squid_it_mcp_scoped_deployment_read")
    {
    }

    [Fact]
    public async Task History_SeparatesLatestAttemptFromLatestSuccess()
    {
        const int userId = 1001;

        await SeedCatalogAsync().ConfigureAwait(false);
        await GrantAsync(userId, roleId: 1101, teamId: 1201, scopedRoleId: 1301, Permission.DeploymentView, TestSpaceId).ConfigureAwait(false);

        await Run<SquidDbContext>(async db =>
        {
            db.Set<ServerTaskEntity>().AddRange(
                TaskEntity(1401, TestProjectId, TestEnvironmentId, TaskState.Success, BaseTime.AddHours(1), BaseTime.AddHours(4)),
                TaskEntity(1402, TestProjectId, TestEnvironmentId, TaskState.Success, BaseTime.AddHours(2), BaseTime.AddHours(3)),
                TaskEntity(1403, TestProjectId, TestEnvironmentId, TaskState.Failed, BaseTime.AddHours(3), BaseTime.AddHours(5)));

            db.Set<DeploymentEntity>().AddRange(
                DeploymentEntity(1501, 1401, 9501, TestProjectId, TestEnvironmentId),
                DeploymentEntity(1502, 1402, 9501, TestProjectId, TestEnvironmentId),
                DeploymentEntity(1503, 1403, 9501, TestProjectId, TestEnvironmentId));

            await db.SaveChangesAsync();

            await SetDeploymentCreatedAtAsync(db, 1501, BaseTime.AddHours(1));
            await SetDeploymentCreatedAtAsync(db, 1502, BaseTime.AddHours(2));
            await SetDeploymentCreatedAtAsync(db, 1503, BaseTime.AddHours(3));
        }).ConfigureAwait(false);

        var firstPage = await QueryAsync<ListVisibleDeploymentsRequest, ListVisibleDeploymentsResponse>(
            userId,
            new ListVisibleDeploymentsRequest
            {
                SpaceId = TestSpaceId,
                ProjectId = TestProjectId,
                EnvironmentId = TestEnvironmentId,
                PageSize = 1
            }).ConfigureAwait(false);

        firstPage.Code.ShouldBe(HttpStatusCode.OK);
        firstPage.Data.Items.Single().Id.ShouldBe(1503);
        firstPage.Data.Items.Single().State.ShouldBe(TaskState.Failed);
        firstPage.Data.NextPosition.ShouldNotBeNull();
        firstPage.LatestSuccessfulDeployment.ShouldNotBeNull();
        firstPage.LatestSuccessfulDeployment.Id.ShouldBe(1502,
            "the summary is selected by deployment creation time and ID, not task completion time");

        var secondPage = await QueryAsync<ListVisibleDeploymentsRequest, ListVisibleDeploymentsResponse>(
            userId,
            new ListVisibleDeploymentsRequest
            {
                SpaceId = TestSpaceId,
                ProjectId = TestProjectId,
                EnvironmentId = TestEnvironmentId,
                PageSize = 2,
                After = firstPage.Data.NextPosition
            }).ConfigureAwait(false);

        secondPage.Data.Items.Select(item => item.Id).ShouldBe([1502, 1501]);
        secondPage.Data.NextPosition.ShouldBeNull();
        secondPage.LatestSuccessfulDeployment.ShouldNotBeNull();
        secondPage.LatestSuccessfulDeployment.Id.ShouldBe(1502);
    }

    [Fact]
    public async Task History_UsesIdAsLatestSuccessTieBreaker()
    {
        const int userId = 1009;

        await SeedCatalogAsync().ConfigureAwait(false);
        await GrantAsync(userId, roleId: 1109, teamId: 1209, scopedRoleId: 1309, Permission.DeploymentView, TestSpaceId).ConfigureAwait(false);

        await Run<SquidDbContext>(async db =>
        {
            db.Set<ServerTaskEntity>().AddRange(
                TaskEntity(1412, TestProjectId, TestEnvironmentId, TaskState.Success, BaseTime.AddMinutes(1), BaseTime.AddMinutes(5)),
                TaskEntity(1413, TestProjectId, TestEnvironmentId, TaskState.Success, BaseTime.AddMinutes(2), BaseTime.AddMinutes(4)));
            db.Set<DeploymentEntity>().AddRange(
                DeploymentEntity(1514, 1412, 9501, TestProjectId, TestEnvironmentId),
                DeploymentEntity(1515, 1413, 9501, TestProjectId, TestEnvironmentId));
            await db.SaveChangesAsync();
            await SetDeploymentCreatedAtAsync(db, 1514, BaseTime.AddMinutes(1));
            await SetDeploymentCreatedAtAsync(db, 1515, BaseTime.AddMinutes(1));
        }).ConfigureAwait(false);

        var response = await QueryAsync<ListVisibleDeploymentsRequest, ListVisibleDeploymentsResponse>(
            userId,
            DeploymentHistoryRequest()).ConfigureAwait(false);

        response.Data.Items.Select(item => item.Id).ShouldBe([1515, 1514]);
        response.LatestSuccessfulDeployment.ShouldNotBeNull();
        response.LatestSuccessfulDeployment.Id.ShouldBe(1515);
    }

    [Fact]
    public async Task History_PreservesDeploymentAfterReleaseRemovalAndShowsCurrentVersionAfterRename()
    {
        const int userId = 1002;
        const int releaseId = 9501;
        const int mismatchedReleaseId = 9502;

        await SeedCatalogAsync().ConfigureAwait(false);
        await GrantAsync(userId, roleId: 1102, teamId: 1202, scopedRoleId: 1302, Permission.DeploymentView, TestSpaceId).ConfigureAwait(false);

        await Run<SquidDbContext>(async db =>
        {
            db.Set<DeploymentReleaseEntity>().Add(ReleaseEntity(releaseId, TestSpaceId, TestProjectId, "1.0.0"));
            db.Set<DeploymentReleaseEntity>().Add(ReleaseEntity(mismatchedReleaseId, TestSpaceId, OtherProjectId, "other-version"));
            db.Set<ServerTaskEntity>().Add(TaskEntity(1404, TestProjectId, TestEnvironmentId, TaskState.Success, BaseTime, BaseTime.AddMinutes(5)));
            db.Set<DeploymentEntity>().AddRange(
                DeploymentEntity(1504, 1404, releaseId, TestProjectId, TestEnvironmentId),
                DeploymentEntity(1505, 1404, mismatchedReleaseId, TestProjectId, TestEnvironmentId));

            await db.SaveChangesAsync();
            await SetDeploymentCreatedAtAsync(db, 1504, BaseTime.AddMinutes(1));
            await SetDeploymentCreatedAtAsync(db, 1505, BaseTime.AddMinutes(2));

            await db.Set<DeploymentReleaseEntity>()
                .Where(release => release.Id == releaseId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(release => release.Version, "2.0.0"));
        }).ConfigureAwait(false);

        var renamed = await QueryAsync<ListVisibleDeploymentsRequest, ListVisibleDeploymentsResponse>(
            userId,
            DeploymentHistoryRequest()).ConfigureAwait(false);

        renamed.Data.Items.Single(item => item.Id == 1504).CurrentReleaseVersion.ShouldBe("2.0.0");
        renamed.Data.Items.Single(item => item.Id == 1505).CurrentReleaseVersion.ShouldBeNull(
            "a release from another project is not a valid deployment association");

        await Run<SquidDbContext>(db => db.Set<DeploymentReleaseEntity>()
            .Where(release => release.Id == releaseId)
            .ExecuteDeleteAsync()).ConfigureAwait(false);

        var removed = await QueryAsync<ListVisibleDeploymentsRequest, ListVisibleDeploymentsResponse>(
            userId,
            DeploymentHistoryRequest()).ConfigureAwait(false);

        removed.Data.Items.ShouldContain(item => item.Id == 1504);
        removed.Data.Items.Single(item => item.Id == 1504).CurrentReleaseVersion.ShouldBeNull();
    }

    [Fact]
    public async Task History_ReturnsUnknownForMissingTask()
    {
        const int userId = 1003;

        await SeedCatalogAsync().ConfigureAwait(false);
        await GrantAsync(userId, roleId: 1103, teamId: 1203, scopedRoleId: 1303, Permission.DeploymentView, TestSpaceId).ConfigureAwait(false);

        await Run<SquidDbContext>(async db =>
        {
            db.Set<DeploymentEntity>().Add(DeploymentEntity(1506, 9999, 9501, TestProjectId, TestEnvironmentId));
            await db.SaveChangesAsync();
            await SetDeploymentCreatedAtAsync(db, 1506, BaseTime);
        }).ConfigureAwait(false);

        var response = await QueryAsync<ListVisibleDeploymentsRequest, ListVisibleDeploymentsResponse>(
            userId,
            DeploymentHistoryRequest()).ConfigureAwait(false);

        var item = response.Data.Items.Single();
        item.Id.ShouldBe(1506);
        item.TaskId.ShouldBe(9999);
        item.State.ShouldBe("Unknown");
        item.CompletedAt.ShouldBeNull();
        response.LatestSuccessfulDeployment.ShouldBeNull();
    }

    [Fact]
    public async Task History_DuplicateTaskAssociationIsUnknownAndExcludedFromSummary()
    {
        const int userId = 1013;

        await SeedCatalogAsync().ConfigureAwait(false);
        await GrantAsync(userId, roleId: 1114, teamId: 1214, scopedRoleId: 1314, Permission.DeploymentView, TestSpaceId).ConfigureAwait(false);

        await Run<SquidDbContext>(async db =>
        {
            db.Set<ServerTaskEntity>().Add(TaskEntity(
                1420, TestProjectId, TestEnvironmentId, TaskState.Success, BaseTime, BaseTime.AddMinutes(5)));
            db.Set<DeploymentEntity>().AddRange(
                DeploymentEntity(1520, 1420, 9501, TestProjectId, TestEnvironmentId),
                DeploymentEntity(1521, 1420, 9501, TestProjectId, TestEnvironmentId));
            await db.SaveChangesAsync();
            await SetDeploymentCreatedAtAsync(db, 1520, BaseTime.AddMinutes(1));
            await SetDeploymentCreatedAtAsync(db, 1521, BaseTime.AddMinutes(2));
        }).ConfigureAwait(false);

        var response = await QueryAsync<ListVisibleDeploymentsRequest, ListVisibleDeploymentsResponse>(
            userId,
            DeploymentHistoryRequest()).ConfigureAwait(false);

        response.Data.Items.Count.ShouldBe(2);
        foreach (var item in response.Data.Items)
        {
            item.TaskId.ShouldBe(1420);
            item.State.ShouldBe("Unknown");
            item.CompletedAt.ShouldBeNull();
        }

        response.LatestSuccessfulDeployment.ShouldBeNull(
            "a task associated with more than one deployment has no unique success summary");
    }

    [Fact]
    public async Task History_DoesNotSummarizeForbiddenDeployment()
    {
        const int userId = 1004;

        await SeedCatalogAsync().ConfigureAwait(false);
        await GrantAsync(userId, roleId: 1104, teamId: 1204, scopedRoleId: 1304, Permission.DeploymentView, TestSpaceId).ConfigureAwait(false);

        await Run<SquidDbContext>(async db =>
        {
            db.Set<ServerTaskEntity>().AddRange(
                TaskEntity(1405, TestProjectId, TestEnvironmentId, TaskState.Failed, BaseTime, BaseTime.AddMinutes(5)),
                TaskEntity(1406, OtherProjectId, OtherEnvironmentId, TaskState.Success, BaseTime, BaseTime.AddMinutes(5)));

            db.Set<DeploymentEntity>().AddRange(
                DeploymentEntity(1507, 1405, 9501, TestProjectId, TestEnvironmentId),
                DeploymentEntity(1508, 1406, 9501, OtherProjectId, OtherEnvironmentId));

            await db.SaveChangesAsync();
            await SetDeploymentCreatedAtAsync(db, 1507, BaseTime.AddMinutes(1));
            await SetDeploymentCreatedAtAsync(db, 1508, BaseTime.AddMinutes(2));
        }).ConfigureAwait(false);

        var response = await QueryAsync<ListVisibleDeploymentsRequest, ListVisibleDeploymentsResponse>(
            userId,
            new ListVisibleDeploymentsRequest
            {
                SpaceId = TestSpaceId,
                ProjectId = TestProjectId,
                EnvironmentId = TestEnvironmentId,
                PageSize = 1
            }).ConfigureAwait(false);

        response.Data.Items.Single().Id.ShouldBe(1507);
        response.Data.NextPosition.ShouldBeNull();
        response.LatestSuccessfulDeployment.ShouldBeNull(
            "the unauthorized cross-environment success must not affect summary selection");
    }

    [Fact]
    public async Task TaskStatus_RequiresMatchingDeploymentAndScopedTaskView()
    {
        const int userId = 1005;
        const int userWithoutTaskView = 1006;

        await SeedCatalogAsync().ConfigureAwait(false);
        await GrantAsync(userId, roleId: 1105, teamId: 1205, scopedRoleId: 1305, Permission.TaskView, TestSpaceId).ConfigureAwait(false);
        await GrantAsync(userWithoutTaskView, roleId: 1106, teamId: 1206, scopedRoleId: 1306, Permission.DeploymentView, TestSpaceId).ConfigureAwait(false);

        await Run<SquidDbContext>(async db =>
        {
            var task = TaskEntity(1408, TestProjectId, TestEnvironmentId, TaskState.Success, BaseTime.AddMinutes(1), BaseTime.AddMinutes(2));
            task.SpaceId = null;
            var orphanTask = TaskEntity(1409, TestProjectId, TestEnvironmentId, TaskState.Success, BaseTime, BaseTime.AddMinutes(1));

            db.Set<ServerTaskEntity>().AddRange(task, orphanTask);
            db.Set<DeploymentEntity>().Add(DeploymentEntity(1510, 1408, 9501, TestProjectId, TestEnvironmentId));
            await db.SaveChangesAsync();
        }).ConfigureAwait(false);

        var deploymentService = new Mock<IDeploymentService>(MockBehavior.Strict);
        var serverTaskService = new Mock<IServerTaskService>(MockBehavior.Strict);
        Action<ContainerBuilder> strictReadServices = builder =>
        {
            builder.RegisterInstance(deploymentService.Object).As<IDeploymentService>().SingleInstance();
            builder.RegisterInstance(serverTaskService.Object).As<IServerTaskService>().SingleInstance();
        };

        var visible = await QueryAsync<GetVisibleDeploymentTaskRequest, GetVisibleDeploymentTaskResponse>(
            userId,
            new GetVisibleDeploymentTaskRequest { SpaceId = TestSpaceId, TaskId = 1408 },
            strictReadServices).ConfigureAwait(false);

        visible.Code.ShouldBe(HttpStatusCode.OK);
        visible.Data.TaskId.ShouldBe(1408);
        visible.Data.DeploymentId.ShouldBe(1510);
        visible.Data.State.ShouldBe(TaskState.Success);
        visible.Data.StartedAt.ShouldBe(BaseTime.AddMinutes(1));
        visible.Data.CompletedAt.ShouldBe(BaseTime.AddMinutes(2));

        var noDeployment = await QueryAsync<GetVisibleDeploymentTaskRequest, GetVisibleDeploymentTaskResponse>(
            userId,
            new GetVisibleDeploymentTaskRequest { SpaceId = TestSpaceId, TaskId = 1409 },
            strictReadServices).ConfigureAwait(false);
        AssertInvisible(noDeployment);

        await Run<SquidDbContext>(db => db.Set<ServerTaskEntity>()
            .Where(task => task.Id == 1408)
            .ExecuteUpdateAsync(setters => setters.SetProperty(task => task.ProjectId, OtherProjectId))).ConfigureAwait(false);

        var mismatchedResources = await QueryAsync<GetVisibleDeploymentTaskRequest, GetVisibleDeploymentTaskResponse>(
            userId,
            new GetVisibleDeploymentTaskRequest { SpaceId = TestSpaceId, TaskId = 1408 },
            strictReadServices).ConfigureAwait(false);
        AssertInvisible(mismatchedResources);

        await Run<SquidDbContext>(async db =>
        {
            await db.Set<ServerTaskEntity>()
                .Where(task => task.Id == 1408)
                .ExecuteUpdateAsync(setters => setters.SetProperty(task => task.ProjectId, TestProjectId));

            db.Set<DeploymentEntity>().Add(DeploymentEntity(1511, 1408, 9501, OtherProjectId, OtherEnvironmentId));
            await db.SaveChangesAsync();
        }).ConfigureAwait(false);

        var conflictingAssociations = await QueryAsync<GetVisibleDeploymentTaskRequest, GetVisibleDeploymentTaskResponse>(
            userId,
            new GetVisibleDeploymentTaskRequest { SpaceId = TestSpaceId, TaskId = 1408 },
            strictReadServices).ConfigureAwait(false);
        AssertInvisible(conflictingAssociations);

        var withoutTaskView = await QueryAsync<GetVisibleDeploymentTaskRequest, GetVisibleDeploymentTaskResponse>(
            userWithoutTaskView,
            new GetVisibleDeploymentTaskRequest { SpaceId = TestSpaceId, TaskId = 1408 },
            strictReadServices).ConfigureAwait(false);
        AssertInvisible(withoutTaskView);

        deploymentService.Invocations.ShouldBeEmpty();
        serverTaskService.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task TaskStatus_WithoutTaskView_IsInvisibleEvenWithDeploymentView()
    {
        const int userId = 1010;

        await SeedCatalogAsync().ConfigureAwait(false);
        await GrantAsync(userId, roleId: 1110, teamId: 1210, scopedRoleId: 1310, Permission.DeploymentView, TestSpaceId).ConfigureAwait(false);
        await SeedMatchingTaskAndDeploymentAsync(1414, 1516).ConfigureAwait(false);

        var response = await QueryAsync<GetVisibleDeploymentTaskRequest, GetVisibleDeploymentTaskResponse>(
            userId,
            new GetVisibleDeploymentTaskRequest { SpaceId = TestSpaceId, TaskId = 1414 }).ConfigureAwait(false);

        AssertInvisible(response);
    }

    [Fact]
    public async Task TaskStatus_TaskViewScopeMismatch_IsInvisible()
    {
        const int userId = 1011;

        await SeedCatalogAsync().ConfigureAwait(false);
        await GrantAsync(
            userId,
            roleId: 1111,
            teamId: 1211,
            scopedRoleId: 1311,
            Permission.TaskView,
            TestSpaceId,
            projectIds: [TestProjectId],
            environmentIds: [OtherEnvironmentId],
            projectGroupIds: [TestProjectGroupId]).ConfigureAwait(false);
        await SeedMatchingTaskAndDeploymentAsync(1415, 1517).ConfigureAwait(false);

        var response = await QueryAsync<GetVisibleDeploymentTaskRequest, GetVisibleDeploymentTaskResponse>(
            userId,
            new GetVisibleDeploymentTaskRequest { SpaceId = TestSpaceId, TaskId = 1415 }).ConfigureAwait(false);

        AssertInvisible(response);
    }

    [Fact]
    public async Task TaskStatus_DoesNotMergeTaskViewDimensionsAcrossRoles()
    {
        const int userId = 1012;

        await SeedCatalogAsync().ConfigureAwait(false);
        await GrantAsync(
            userId,
            roleId: 1112,
            teamId: 1212,
            scopedRoleId: 1312,
            Permission.TaskView,
            TestSpaceId,
            projectIds: [TestProjectId],
            environmentIds: [OtherEnvironmentId],
            projectGroupIds: [TestProjectGroupId]).ConfigureAwait(false);
        await GrantAsync(
            userId,
            roleId: 1113,
            teamId: 1213,
            scopedRoleId: 1313,
            Permission.TaskView,
            TestSpaceId,
            projectIds: [SecondTestProjectId],
            environmentIds: [TestEnvironmentId],
            projectGroupIds: [TestProjectGroupId]).ConfigureAwait(false);
        await SeedMatchingTaskAndDeploymentAsync(1416, 1518).ConfigureAwait(false);

        var response = await QueryAsync<GetVisibleDeploymentTaskRequest, GetVisibleDeploymentTaskResponse>(
            userId,
            new GetVisibleDeploymentTaskRequest { SpaceId = TestSpaceId, TaskId = 1416 }).ConfigureAwait(false);

        AssertInvisible(response);
    }

    [Fact]
    public async Task TaskStatus_NormalizesUnknownStoredState()
    {
        const int userId = 1007;

        await SeedCatalogAsync().ConfigureAwait(false);
        await GrantAsync(userId, roleId: 1107, teamId: 1207, scopedRoleId: 1307, Permission.TaskView, TestSpaceId).ConfigureAwait(false);

        await Run<SquidDbContext>(async db =>
        {
            var task = TaskEntity(1410, TestProjectId, TestEnvironmentId, "NotAStoredState", BaseTime, BaseTime.AddMinutes(1));
            task.SpaceId = TestSpaceId;
            db.Set<ServerTaskEntity>().Add(task);
            db.Set<DeploymentEntity>().Add(DeploymentEntity(1512, 1410, 9501, TestProjectId, TestEnvironmentId));
            await db.SaveChangesAsync();
        }).ConfigureAwait(false);

        var response = await QueryAsync<GetVisibleDeploymentTaskRequest, GetVisibleDeploymentTaskResponse>(
            userId,
            new GetVisibleDeploymentTaskRequest { SpaceId = TestSpaceId, TaskId = 1410 }).ConfigureAwait(false);

        response.Code.ShouldBe(HttpStatusCode.OK);
        response.Data.State.ShouldBe("Unknown");
    }

    [Fact]
    public async Task History_UsesCurrentProjectGroupForAuthorization()
    {
        const int userId = 1013;

        await SeedCatalogAsync().ConfigureAwait(false);
        await GrantAsync(userId, roleId: 1114, teamId: 1214, scopedRoleId: 1314, Permission.DeploymentView, TestSpaceId).ConfigureAwait(false);
        await SeedMatchingTaskAndDeploymentAsync(1417, 1519).ConfigureAwait(false);

        await Run<SquidDbContext>(db => db.Set<DeploymentProjectEntity>()
            .Where(project => project.Id == TestProjectId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(project => project.ProjectGroupId, ReplacementProjectGroupId))).ConfigureAwait(false);

        var response = await QueryAsync<ListVisibleDeploymentsRequest, ListVisibleDeploymentsResponse>(
            userId,
            DeploymentHistoryRequest()).ConfigureAwait(false);

        AssertListInvisible(response);
    }

    [Fact]
    public async Task History_UsesSameStateNormalizationForFilteringAndSummary()
    {
        const int userId = 1008;

        await SeedCatalogAsync().ConfigureAwait(false);
        await GrantAsync(userId, roleId: 1108, teamId: 1208, scopedRoleId: 1308, Permission.DeploymentView, TestSpaceId).ConfigureAwait(false);

        await Run<SquidDbContext>(async db =>
        {
            db.Set<ServerTaskEntity>().Add(TaskEntity(1411, TestProjectId, TestEnvironmentId, "success", BaseTime, BaseTime.AddMinutes(1)));
            db.Set<DeploymentEntity>().Add(DeploymentEntity(1513, 1411, 9501, TestProjectId, TestEnvironmentId));
            await db.SaveChangesAsync();
            await SetDeploymentCreatedAtAsync(db, 1513, BaseTime);
        }).ConfigureAwait(false);

        var response = await QueryAsync<ListVisibleDeploymentsRequest, ListVisibleDeploymentsResponse>(
            userId,
            DeploymentHistoryRequest()).ConfigureAwait(false);

        response.Data.Items.Single().State.ShouldBe(TaskState.Success);
        response.LatestSuccessfulDeployment.ShouldNotBeNull();
        response.LatestSuccessfulDeployment.Id.ShouldBe(1513);
    }

    [Fact]
    public void ReadItems_ExposeOnlyWhitelistedFields()
    {
        typeof(DeploymentReadItem).GetProperties().Select(property => property.Name).ShouldBe(
            ["Id", "ReleaseId", "CurrentReleaseVersion", "TaskId", "State", "CreatedAt", "CompletedAt"]);
        typeof(DeploymentTaskReadItem).GetProperties().Select(property => property.Name).ShouldBe(
            ["TaskId", "DeploymentId", "State", "StartedAt", "CompletedAt"]);
    }

    private static void AssertInvisible(GetVisibleDeploymentTaskResponse response)
    {
        response.Code.ShouldBe(HttpStatusCode.NotFound);
        response.Msg.ShouldBe("Resource not found or inaccessible.");
        response.Data.ShouldBeNull();
    }

    private static void AssertListInvisible(ListVisibleDeploymentsResponse response)
    {
        response.Code.ShouldBe(HttpStatusCode.NotFound);
        response.Msg.ShouldBe("Resource not found or inaccessible.");
        response.Data.ShouldBeNull();
        response.LatestSuccessfulDeployment.ShouldBeNull();
    }

    private async Task SeedMatchingTaskAndDeploymentAsync(int taskId, int deploymentId)
    {
        await Run<SquidDbContext>(async db =>
        {
            var task = TaskEntity(taskId, TestProjectId, TestEnvironmentId, TaskState.Success, BaseTime,
                BaseTime.AddMinutes(1));
            task.SpaceId = null;
            db.Set<ServerTaskEntity>().Add(task);
            db.Set<DeploymentEntity>().Add(DeploymentEntity(deploymentId, taskId, 9501, TestProjectId,
                TestEnvironmentId));
            await db.SaveChangesAsync();
        }).ConfigureAwait(false);
    }

    private static ListVisibleDeploymentsRequest DeploymentHistoryRequest()
    {
        return new ListVisibleDeploymentsRequest
        {
            SpaceId = TestSpaceId,
            ProjectId = TestProjectId,
            EnvironmentId = TestEnvironmentId,
            PageSize = 20
        };
    }

    private static ServerTaskEntity TaskEntity(
        int id,
        int projectId,
        int environmentId,
        string state,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt)
    {
        return new ServerTaskEntity
        {
            Id = id,
            Name = $"Task-{id}",
            Description = $"Task {id}",
            QueueTime = BaseTime,
            StartTime = startedAt,
            CompletedTime = completedAt,
            ErrorMessage = "sensitive error text",
            ConcurrencyTag = null,
            State = state,
            HasWarningsOrErrors = true,
            ServerNodeId = Guid.NewGuid(),
            ProjectId = projectId,
            EnvironmentId = environmentId,
            DurationSeconds = 60,
            BatchId = 0,
            JSON = """{"sensitive":"execution payload"}""",
            DataVersion = new byte[16],
            SpaceId = TestSpaceId,
            BusinessProcessState = "Completed",
            ServerTaskType = "Deploy",
            StateOrder = 1,
            Weight = 1,
            HasPendingInterruptions = false
        };
    }

    private static DeploymentEntity DeploymentEntity(
        int id,
        int? taskId,
        int releaseId,
        int projectId,
        int environmentId)
    {
        return new DeploymentEntity
        {
            Id = id,
            Name = $"Deployment-{id}",
            TaskId = taskId,
            SpaceId = TestSpaceId,
            ChannelId = 1,
            ProjectId = projectId,
            ReleaseId = releaseId,
            EnvironmentId = environmentId,
            MachineId = 0,
            Json = """{"sensitive":"deployment payload"}""",
            DeployedBy = 1001,
            DeployedToMachineIds = string.Empty,
            CreatedDate = BaseTime
        };
    }

    private static DeploymentReleaseEntity ReleaseEntity(int id, int spaceId, int projectId, string version)
    {
        return new DeploymentReleaseEntity
        {
            Id = id,
            Version = version,
            ProjectId = projectId,
            ProjectVariableSetSnapshotId = 0,
            ProjectDeploymentProcessSnapshotId = 0,
            ChannelId = 1,
            SpaceId = spaceId,
            ReleaseNote = string.Empty,
            CreatedDate = BaseTime
        };
    }

    private async Task SeedCatalogAsync()
    {
        await Run<SquidDbContext>(async db =>
        {
            db.Set<DeploymentSpaceEntity>().AddRange(
                SpaceEntity(TestSpaceId, "MCP Test Space"),
                SpaceEntity(OtherSpaceId, "MCP Other Space"));

            db.Set<DeploymentProjectGroupEntity>().AddRange(
                ProjectGroupEntity(TestProjectGroupId, TestSpaceId, "MCP Group"),
                ProjectGroupEntity(OtherProjectGroupId, OtherSpaceId, "Other Group"),
                ProjectGroupEntity(ReplacementProjectGroupId, TestSpaceId, "Replacement Group"));

            db.Set<DeploymentProjectEntity>().AddRange(
                ProjectEntity(TestProjectId, TestSpaceId, TestProjectGroupId, "mcp-project"),
                ProjectEntity(OtherProjectId, OtherSpaceId, OtherProjectGroupId, "other-project"),
                ProjectEntity(SecondTestProjectId, TestSpaceId, TestProjectGroupId, "second-mcp-project"));

            db.Set<DeploymentEnvironmentEntity>().AddRange(
                EnvironmentEntity(TestEnvironmentId, TestSpaceId, "mcp-env"),
                EnvironmentEntity(OtherEnvironmentId, OtherSpaceId, "other-env"));

            await db.SaveChangesAsync();
        }).ConfigureAwait(false);
    }

    private async Task GrantAsync(
        int userId,
        int roleId,
        int teamId,
        int scopedRoleId,
        Permission permission,
        int? spaceId,
        IReadOnlyCollection<int> projectIds = null,
        IReadOnlyCollection<int> environmentIds = null,
        IReadOnlyCollection<int> projectGroupIds = null)
    {
        projectIds ??= [TestProjectId];
        environmentIds ??= [TestEnvironmentId];
        projectGroupIds ??= [TestProjectGroupId];

        await Run<SquidDbContext>(async db =>
        {
            db.Set<Team>().Add(new Team
            {
                Id = teamId,
                Name = $"Team-{teamId}",
                Description = string.Empty,
                SpaceId = spaceId ?? 0,
                IsBuiltIn = false
            });
            db.Set<TeamMember>().Add(new TeamMember { TeamId = teamId, UserId = userId });
            db.Set<UserRole>().Add(new UserRole
            {
                Id = roleId,
                Name = $"Role-{roleId}",
                Description = string.Empty,
                IsBuiltIn = false
            });
            db.Set<UserRolePermission>().Add(new UserRolePermission
            {
                UserRoleId = roleId,
                Permission = permission.ToString()
            });
            await db.SaveChangesAsync();

            db.Set<ScopedUserRole>().Add(new ScopedUserRole
            {
                Id = scopedRoleId,
                TeamId = teamId,
                UserRoleId = roleId,
                SpaceId = spaceId
            });
            await db.SaveChangesAsync();

            if (projectIds.Count > 0)
            {
                db.Set<ScopedUserRoleProject>().AddRange(projectIds.Select(projectId =>
                    new ScopedUserRoleProject { ScopedUserRoleId = scopedRoleId, ProjectId = projectId }));
            }

            if (environmentIds.Count > 0)
            {
                db.Set<ScopedUserRoleEnvironment>().AddRange(environmentIds.Select(environmentId =>
                    new ScopedUserRoleEnvironment { ScopedUserRoleId = scopedRoleId, EnvironmentId = environmentId }));
            }

            if (projectGroupIds.Count > 0)
            {
                db.Set<ScopedUserRoleProjectGroup>().AddRange(projectGroupIds.Select(projectGroupId =>
                    new ScopedUserRoleProjectGroup
                    {
                        ScopedUserRoleId = scopedRoleId,
                        ProjectGroupId = projectGroupId
                    }));
            }

            await db.SaveChangesAsync();
        }).ConfigureAwait(false);
    }

    private async Task<TResponse> QueryAsync<TRequest, TResponse>(
        int userId,
        TRequest request,
        Action<ContainerBuilder> extraRegistration = null)
        where TRequest : IRequest
        where TResponse : IResponse
    {
        TResponse response = default;

        await Run<IMediator>(
            async mediator =>
            {
                response = await mediator.RequestAsync<TRequest, TResponse>(request).ConfigureAwait(false);
            },
            builder =>
            {
                builder.RegisterInstance<ICurrentUser>(new FixedCurrentUser(userId)).SingleInstance();
                extraRegistration?.Invoke(builder);
            }).ConfigureAwait(false);

        return response;
    }

    private static async Task SetDeploymentCreatedAtAsync(SquidDbContext db, int deploymentId, DateTimeOffset createdAt)
    {
        await db.Set<DeploymentEntity>()
            .Where(deployment => deployment.Id == deploymentId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(deployment => deployment.CreatedDate, createdAt))
            .ConfigureAwait(false);
    }

    private static DeploymentSpaceEntity SpaceEntity(int id, string name)
    {
        return new DeploymentSpaceEntity
        {
            Id = id,
            Name = name,
            Slug = name.ToLowerInvariant().Replace(' ', '-'),
            IsDefault = false,
            Json = "{}",
            TaskQueueStopped = false,
            Description = string.Empty
        };
    }

    private static DeploymentProjectGroupEntity ProjectGroupEntity(int id, int spaceId, string name)
    {
        return new DeploymentProjectGroupEntity
        {
            Id = id,
            Name = name,
            Description = string.Empty,
            SpaceId = spaceId,
            Slug = name.ToLowerInvariant().Replace(' ', '-')
        };
    }

    private static DeploymentProjectEntity ProjectEntity(int id, int spaceId, int projectGroupId, string slug)
    {
        return new DeploymentProjectEntity
        {
            Id = id,
            Name = slug,
            Slug = slug,
            IsDisabled = false,
            VariableSetId = 0,
            DeploymentProcessId = 0,
            ProjectGroupId = projectGroupId,
            LifecycleId = 0,
            AutoCreateRelease = false,
            Json = "{}",
            IncludedLibraryVariableSetIds = "[]",
            DiscreteChannelRelease = false,
            SpaceId = spaceId,
            AllowIgnoreChannelRules = true
        };
    }

    private static DeploymentEnvironmentEntity EnvironmentEntity(int id, int spaceId, string slug)
    {
        return new DeploymentEnvironmentEntity
        {
            Id = id,
            SpaceId = spaceId,
            Slug = slug,
            Name = slug,
            Description = string.Empty,
            SortOrder = 0,
            UseGuidedFailure = false,
            AllowDynamicInfrastructure = false
        };
    }

    private sealed class FixedCurrentUser(int id) : ICurrentUser
    {
        public int? Id { get; } = id;
        public string Name => "integration-test-user";
        public bool IsInternal => false;
    }
}
