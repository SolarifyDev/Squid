using Microsoft.EntityFrameworkCore;
using Npgsql;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Runbooks;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums;
using SquidProject = Squid.Core.Persistence.Entities.Deployments.Project;

namespace Squid.IntegrationTests.Services.Deployments.Runbooks;

public class RunbookFoundationMigrationTests : TestBase
{
    public RunbookFoundationMigrationTests()
        : base("_runbook_foundation_", "squid_it_runbook_foundation_20261104")
    {
    }

    [Fact]
    public async Task Migration_KeepsProjectDeploymentProcessAndVariableSetReadable()
    {
        await Run<SquidDbContext>(async db =>
        {
            var project = await SeedProjectAsync(db);
            var process = await db.Set<DeploymentProcess>().SingleAsync(p => p.Id == project.DeploymentProcessId);
            var variableSet = await db.Set<VariableSet>().SingleAsync(v => v.Id == project.VariableSetId);

            process.ProcessKind.ShouldBe(DeploymentProcessKind.ProjectDeployment);
            process.OwnerId.ShouldBeNull();
            variableSet.OwnerType.ShouldBe(VariableSetOwnerType.Project);
            variableSet.OwnerId.ShouldBe(project.Id);
        });
    }

    [Fact]
    public async Task ProcessOwnershipCheck_RejectsInvalidRunbookShape()
    {
        await Run<SquidDbContext>(async db =>
        {
            var process = await SeedProjectAsync(db);
            var updateException = await Should.ThrowAsync<PostgresException>(
                () => db.Database.ExecuteSqlRawAsync(
                    $"""
                    UPDATE deployment_process
                    SET process_kind = 1, owner_id = NULL
                    WHERE id = {process.DeploymentProcessId}
                    """));
            updateException.SqlState.ShouldBe("23514");
            updateException.ConstraintName.ShouldBe("CheckRunbookOwnership");
        });
    }

    [Fact]
    public async Task ProcessOwnershipUniqueIndex_RejectsSecondProcessForSameRunbook()
    {
        await Run<SquidDbContext>(async db =>
        {
            await AssertPostgresErrorAsync(
                db,
                """
                INSERT INTO deployment_process (project_id, space_id, process_kind, owner_id)
                VALUES
                    (1, 1, 1, 1001),
                    (1, 1, 1, 1001)
                """,
                "23505",
                "ux_deployment_process_runbook_owner");
        });
    }

    [Fact]
    public async Task RunbookUniqueness_RejectsDuplicateNormalizedName()
    {
        await Run<SquidDbContext>(async db =>
        {
            var projectId = await GetNextIdentityAsync(db, "project_id_seq");
            await AssertPostgresErrorAsync(
                db,
                $"""
                INSERT INTO runbook (project_id, space_id, name, process_id, variable_set_id)
                VALUES
                    ({projectId}, 1, 'Emergency Restore', 1, 1),
                    ({projectId}, 1, '  emergency restore  ', 2, 2)
                """,
                "23505",
                "ux_runbook_project_normalized_name");
        });
    }

    [Fact]
    public async Task RunbookUniqueness_RejectsSharedProcess()
    {
        await Run<SquidDbContext>(async db =>
        {
            var projectId = await GetNextIdentityAsync(db, "project_id_seq");
            await AssertPostgresErrorAsync(
                db,
                $"""
                INSERT INTO runbook (project_id, space_id, name, process_id, variable_set_id)
                VALUES
                    ({projectId}, 1, 'First Name', 1, 1),
                    ({projectId}, 1, 'Second Name', 1, 2)
                """,
                "23505",
                "ux_runbook_process_id");
        });
    }

    [Fact]
    public async Task RunbookUniqueness_RejectsSharedVariableSet()
    {
        await Run<SquidDbContext>(async db =>
        {
            var projectId = await GetNextIdentityAsync(db, "project_id_seq");
            await AssertPostgresErrorAsync(
                db,
                $"""
                INSERT INTO runbook (project_id, space_id, name, process_id, variable_set_id)
                VALUES
                    ({projectId}, 1, 'First Name', 1, 1),
                    ({projectId}, 1, 'Second Name', 2, 1)
                """,
                "23505",
                "ux_runbook_variable_set_id");
        });
    }

    [Fact]
    public async Task RunbookDataProvider_ReturnsOnlyMatchingOwnershipChains()
    {
        await Run<SquidDbContext, IRunbookDataProvider>(async (db, provider) =>
        {
            var project = await SeedProjectAsync(db);
            var runbook = await SeedRunbookAggregateAsync(db, project, "Restore Service");
            var otherProject = await SeedProjectAsync(db, "Other Project", "other-project");
            var otherRunbook = await SeedRunbookAggregateAsync(db, otherProject, "Restore Service");
            db.ChangeTracker.Clear();

            var ownProcess = await provider.GetRunbookProcessAsync(
                runbook.Id, runbook.ProcessId, project.Id, project.SpaceId);
            ownProcess.ShouldNotBeNull();
            ownProcess.OwnerId.ShouldBe(runbook.Id);

            var wrongProcess = await provider.GetRunbookProcessAsync(
                runbook.Id, otherRunbook.ProcessId, project.Id, project.SpaceId);
            wrongProcess.ShouldBeNull();

            var wrongProject = await provider.GetRunbookProcessAsync(
                runbook.Id, runbook.ProcessId, otherProject.Id, project.SpaceId);
            wrongProject.ShouldBeNull();

            var ownVariableSet = await provider.GetRunbookVariableSetAsync(runbook.Id, project.SpaceId);
            ownVariableSet.ShouldNotBeNull();
            ownVariableSet.OwnerType.ShouldBe(VariableSetOwnerType.Runbook);
            ownVariableSet.OwnerId.ShouldBe(runbook.Id);

            var wrongVariableSetSpace = await provider.GetRunbookVariableSetAsync(
                runbook.Id, project.SpaceId + otherProject.SpaceId);
            wrongVariableSetSpace.ShouldBeNull();

            var projectProcess = await provider.GetProjectDeploymentProcessAsync(
                project.Id, project.DeploymentProcessId, project.SpaceId);
            projectProcess.ShouldNotBeNull();
            projectProcess.ProcessKind.ShouldBe(DeploymentProcessKind.ProjectDeployment);
            projectProcess.OwnerId.ShouldBeNull();

            var wrongProjectProcessId = await provider.GetProjectDeploymentProcessAsync(
                project.Id, runbook.ProcessId, project.SpaceId);
            wrongProjectProcessId.ShouldBeNull();

            var (count, page) = await provider.GetRunbookPagingAsync(
                project.Id, project.SpaceId, pageIndex: 1, pageSize: 10);
            count.ShouldBe(1);
            page.Single().Id.ShouldBe(runbook.Id);
        });
    }

    private static async Task<SquidProject> SeedProjectAsync(
        SquidDbContext db,
        string name = "Runbook Project",
        string slug = "runbook-project")
    {
        var spaceId = await GetNextIdentityAsync(db, "space_id_seq");
        var process = new DeploymentProcess
        {
            SpaceId = spaceId,
            ProcessKind = DeploymentProcessKind.ProjectDeployment,
            OwnerId = null
        };
        db.Set<DeploymentProcess>().Add(process);
        await db.SaveChangesAsync();

        var variableSet = new VariableSet
        {
            Name = name,
            OwnerType = VariableSetOwnerType.Project,
            OwnerId = 0,
            SpaceId = process.SpaceId,
            RelatedDocumentIds = "[]"
        };
        db.Set<VariableSet>().Add(variableSet);
        await db.SaveChangesAsync();

        var project = new SquidProject
        {
            Name = name,
            Slug = slug,
            DeploymentProcessId = process.Id,
            VariableSetId = variableSet.Id,
            ProjectGroupId = 1,
            LifecycleId = 1,
            AutoCreateRelease = false,
            Json = string.Empty,
            IncludedLibraryVariableSetIds = "[]",
            SpaceId = process.SpaceId
        };
        db.Set<SquidProject>().Add(project);
        await db.SaveChangesAsync();

        process.ProjectId = project.Id;
        variableSet.OwnerId = project.Id;
        await db.SaveChangesAsync();
        return project;
    }

    private static async Task<Runbook> SeedRunbookAggregateAsync(
        SquidDbContext db,
        SquidProject project,
        string name)
    {
        var (process, variableSet, runbookId) = await ReserveRunbookOwnershipAsync(db, project);
        var runbook = new Runbook
        {
            Id = runbookId,
            ProjectId = project.Id,
            SpaceId = project.SpaceId,
            Name = name,
            Description = "Runbook for integration coverage",
            TagsJson = """["recovery"]""",
            ProcessId = process.Id,
            VariableSetId = variableSet.Id
        };
        db.Set<Runbook>().Add(runbook);
        await db.SaveChangesAsync();
        return runbook;
    }

    private static async Task<(DeploymentProcess Process, VariableSet VariableSet, int RunbookId)>
        ReserveRunbookOwnershipAsync(SquidDbContext db, SquidProject project)
    {
        var runbookId = await GetNextIdentityAsync(db, "runbook_id_seq");
        var process = await CreateRunbookProcessAsync(db, project, runbookId);
        var variableSet = await CreateRunbookVariableSetAsync(db, project, runbookId);
        return (process, variableSet, runbookId);
    }

    private static async Task<int> GetNextIdentityAsync(SquidDbContext db, string sequenceName)
    {
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT nextval('{sequenceName}')";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task AssertPostgresErrorAsync(
        SquidDbContext db,
        string sql,
        string expectedSqlState,
        string expectedConstraint)
    {
        var exception = await Should.ThrowAsync<PostgresException>(
            () => db.Database.ExecuteSqlRawAsync(sql));
        exception.SqlState.ShouldBe(expectedSqlState);
        exception.ConstraintName.ShouldBe(expectedConstraint);
    }

    private static async Task<DeploymentProcess> CreateRunbookProcessAsync(
        SquidDbContext db,
        SquidProject project,
        int ownerId)
    {
        var process = new DeploymentProcess
        {
            ProjectId = project.Id,
            SpaceId = project.SpaceId,
            ProcessKind = DeploymentProcessKind.Runbook,
            OwnerId = ownerId
        };
        db.Set<DeploymentProcess>().Add(process);
        await db.SaveChangesAsync();
        return process;
    }

    private static async Task<VariableSet> CreateRunbookVariableSetAsync(
        SquidDbContext db,
        SquidProject project,
        int ownerId)
    {
        var variableSet = new VariableSet
        {
            Name = "Runbook Variables",
            OwnerType = VariableSetOwnerType.Runbook,
            OwnerId = ownerId,
            SpaceId = project.SpaceId,
            RelatedDocumentIds = "[]"
        };
        db.Set<VariableSet>().Add(variableSet);
        await db.SaveChangesAsync();
        return variableSet;
    }

}
