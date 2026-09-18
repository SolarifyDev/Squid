using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using SquidEnvironment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.IntegrationTests.Services.OctopusImport;

public class OctopusImportTransactionIntegrationTests : TestBase
{
    public OctopusImportTransactionIntegrationTests()
        : base("OctopusImportTransaction", "squid_it_octopus_import_transaction")
    {
    }

    [Fact]
    public async Task BuildPreviewAsync_WithRealArchiveAndDatabase_BuildsCreatePlan()
    {
        var groupJson = """{"Id":"ProjectGroups-1","Name":"Imported Group","Slug":"imported-group","Description":"Imported through the planning pipeline"}""";
        var archivePath = CreateArchive(("ProjectGroups-1", "ProjectGroup", "ProjectGroups-1.json", groupJson));

        try
        {
            await Run<IOctopusImportPlanningPipeline>(async pipeline =>
            {
                var snapshot = await pipeline.BuildPreviewAsync(archivePath, 7);

                snapshot.Graph.Diagnostics.ShouldNotContain(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);
                snapshot.DependencyPlan.Diagnostics.ShouldNotContain(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);
                var resource = snapshot.Graph.Resources.Single();
                resource.Kind.ShouldBe(OctopusResourceKind.ProjectGroup);
                snapshot.DependencyPlan.OrderedResources.Single().SourceId.ShouldBe(resource.SourceId);
                snapshot.PreviewPlan.Resources.Single().PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
            });
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task BuildPreviewAsync_WithUnsupportedCompatibilityDocuments_ReportsEveryResource()
    {
        var archivePath = CreateArchive(
            ("ActionTemplates-1", "ActionTemplate", "ActionTemplates-1.json", """{"Id":"ActionTemplates-1","Name":"Shared template"}"""),
            ("Tenants-1", "Tenant", "Tenants-1.json", """{"Id":"Tenants-1","Name":"Customer A"}"""),
            ("Runbooks-1", "Runbook", "Runbooks-1.json", """{"Id":"Runbooks-1","Name":"Emergency rollback"}"""),
            ("ProjectTriggers-1", "ProjectTrigger", "ProjectTriggers-1.json", """{"Id":"ProjectTriggers-1","Name":"Scheduled deployment"}"""));

        try
        {
            await Run<IOctopusImportPlanningPipeline>(async pipeline =>
            {
                var snapshot = await pipeline.BuildPreviewAsync(archivePath, 7);

                snapshot.Graph.Resources.Select(resource => resource.Kind).ShouldBe([
                    OctopusResourceKind.ActionTemplate,
                    OctopusResourceKind.Tenant,
                    OctopusResourceKind.Runbook,
                    OctopusResourceKind.Trigger
                ], ignoreOrder: true);
                snapshot.PreviewPlan.Resources.Count.ShouldBe(4);
                snapshot.PreviewPlan.Resources.ShouldAllBe(resource =>
                    resource.PreviewAction == OctopusImportPreviewAction.Unsupported &&
                    resource.Diagnostics.Any(diagnostic =>
                        diagnostic.Code == OctopusImportPreviewDiagnosticCodes.ResourceUnsupported));
            });
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task ExecuteInImportTransactionAsync_WhenImportFails_RollsBackResourcesAndSessionResultTogether()
    {
        var sessionId = Guid.NewGuid();

        await Run<SquidDbContext, IOctopusImportTransactionExecutor>(async (db, executor) =>
        {
            db.Set<OctopusImportSession>().Add(new OctopusImportSession
            {
                SessionId = sessionId,
                DestinationSpaceId = 7,
                OwnerUserId = 42,
                State = "Importing",
                SourceSummaryJson = "{}",
                DataVersion = Guid.NewGuid().ToByteArray(),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                LastStateChangedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();

            await Should.ThrowAsync<InvalidOperationException>(() =>
                executor.ExecuteInImportTransactionAsync(
                    new OctopusImportTransactionContext(sessionId, 7),
                    async (_, ct) =>
                    {
                        db.Set<ProjectGroup>().Add(new ProjectGroup
                        {
                            Name = "Imported Group",
                            Description = string.Empty,
                            Slug = "imported-group",
                            SpaceId = 7
                        });
                        var session = await db.Set<OctopusImportSession>()
                            .SingleAsync(s => s.SessionId == sessionId, ct);
                        session.State = "Succeeded";
                        session.ResultJson = "{\"succeeded\":true}";
                        await db.SaveChangesAsync(ct);

                        throw new InvalidOperationException("boom");
                    }));
        });

        await Run<SquidDbContext>(async db =>
        {
            (await db.Set<ProjectGroup>().CountAsync()).ShouldBe(0);
            var session = await db.Set<OctopusImportSession>()
                .AsNoTracking()
                .SingleAsync(s => s.SessionId == sessionId);
            session.State.ShouldBe("Importing");
            session.ResultJson.ShouldBeNull();
        });
    }

    [Fact]
    public async Task ConfirmAsync_WhenPlanContainsProjectGroup_PersistsResourceAndSucceededSession()
    {
        var sessionId = Guid.NewGuid();

        await Run<SquidDbContext, IOctopusImportConfirmationOrchestrator>(async (db, orchestrator) =>
        {
            db.Set<OctopusImportSession>().Add(new OctopusImportSession
            {
                SessionId = sessionId,
                DestinationSpaceId = 7,
                OwnerUserId = CurrentUsers.InternalUser.Id,
                State = OctopusImportSessionState.Validated.ToString(),
                SourceSummaryJson = "{}",
                DataVersion = Guid.NewGuid().ToByteArray(),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                LastStateChangedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var resource = new OctopusResourceNode(
                "ProjectGroups-1",
                "Imported Group",
                OctopusResourceKind.ProjectGroup,
                OctopusDocumentKind.ProjectGroup,
                "ProjectGroups-1.json",
                null,
                null,
                false,
                new OctopusProjectGroupDto
                {
                    Id = "ProjectGroups-1",
                    Name = "Imported Group",
                    Slug = "imported-group",
                    Description = "Imported through the confirmation orchestrator"
                });
            var graph = new OctopusResourceGraph([resource], [], [], []);
            var dependencyPlan = new OctopusImportDependencyPlan([resource], [], [], []);
            var previewPlan = new OctopusImportPreviewPlanDto
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                Resources =
                [
                    new OctopusImportResourceResultDto
                    {
                        SourceId = resource.SourceId,
                        SourceType = resource.Kind.ToString(),
                        SourceName = resource.Name,
                        PreviewAction = OctopusImportPreviewAction.Create,
                        OutcomeState = OctopusImportResourceOutcomeState.Pending
                    }
                ]
            };

            var result = await orchestrator.ConfirmAsync(new OctopusImportConfirmationRequest(
                sessionId,
                7,
                graph,
                dependencyPlan,
                previewPlan));

            result.State.ShouldBe(OctopusImportSessionState.Succeeded);
            result.Result.Succeeded.ShouldBeTrue();
            result.Result.IdMappings.Single().SourceId.ShouldBe(resource.SourceId);
        });

        await Run<SquidDbContext>(async db =>
        {
            var group = await db.Set<ProjectGroup>().AsNoTracking().SingleAsync();
            group.Name.ShouldBe("Imported Group");
            group.SpaceId.ShouldBe(7);

            var session = await db.Set<OctopusImportSession>()
                .AsNoTracking()
                .SingleAsync(s => s.SessionId == sessionId);
            session.State.ShouldBe(OctopusImportSessionState.Succeeded.ToString());
            using var resultDocument = JsonDocument.Parse(session.ResultJson);
            resultDocument.RootElement.GetProperty("succeeded").GetBoolean().ShouldBeTrue();
        });
    }

    [Fact]
    public async Task ConfirmAsync_WhenLaterResourceMappingFails_RollsBackEarlierResourceAndRecordsFailedSession()
    {
        var sessionId = Guid.NewGuid();

        await Run<SquidDbContext, IOctopusImportConfirmationOrchestrator>(async (db, orchestrator) =>
        {
            db.Set<OctopusImportSession>().Add(CreateSession(sessionId, OctopusImportSessionState.Validated));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var group = new OctopusResourceNode(
                "ProjectGroups-rollback",
                "Rolled Back Group",
                OctopusResourceKind.ProjectGroup,
                OctopusDocumentKind.ProjectGroup,
                "ProjectGroups-rollback.json",
                null,
                null,
                false,
                new OctopusProjectGroupDto
                {
                    Id = "ProjectGroups-rollback",
                    Name = "Rolled Back Group",
                    Slug = "rolled-back-group"
                });
            var environment = new OctopusResourceNode(
                "Environments-rollback",
                "Rolled Back Environment",
                OctopusResourceKind.Environment,
                OctopusDocumentKind.Environment,
                "Environments-rollback.json",
                null,
                null,
                false,
                new OctopusEnvironmentDto
                {
                    Id = "Environments-rollback",
                    Name = "Rolled Back Environment",
                    Slug = "rolled-back-environment"
                });
            var project = new OctopusResourceNode(
                "Projects-failing",
                "Failing Project",
                OctopusResourceKind.Project,
                OctopusDocumentKind.Project,
                "Projects-failing.json",
                null,
                null,
                false,
                new OctopusProjectDto
                {
                    Id = "Projects-failing",
                    Name = "Failing Project",
                    Slug = "failing-project",
                    ProjectGroupId = "ProjectGroups-missing",
                    LifecycleId = "Lifecycles-missing"
                });
            var resources = new[] { group, environment, project };
            var graph = new OctopusResourceGraph(resources, [], [], []);
            var dependencyPlan = new OctopusImportDependencyPlan(resources, [], [], []);
            var previewPlan = new OctopusImportPreviewPlanDto
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                Resources = resources.Select(CreatePreviewResource).ToList()
            };

            var result = await orchestrator.ConfirmAsync(new OctopusImportConfirmationRequest(
                sessionId,
                7,
                graph,
                dependencyPlan,
                previewPlan));

            result.State.ShouldBe(OctopusImportSessionState.Failed);
            result.Result.Succeeded.ShouldBeFalse();
        });

        await Run<SquidDbContext>(async db =>
        {
            (await db.Set<ProjectGroup>().CountAsync()).ShouldBe(0);
            (await db.Set<SquidEnvironment>().CountAsync()).ShouldBe(0);
            (await db.Set<Project>().CountAsync()).ShouldBe(0);

            var session = await db.Set<OctopusImportSession>()
                .AsNoTracking()
                .SingleAsync(s => s.SessionId == sessionId);
            session.State.ShouldBe(OctopusImportSessionState.Failed.ToString());
            using var resultDocument = JsonDocument.Parse(session.ResultJson);
            resultDocument.RootElement.GetProperty("succeeded").GetBoolean().ShouldBeFalse();
        });
    }

    [Fact]
    public async Task TryStartConfirmationAsync_WhenTwoCallersRace_AdmitsExactlyOne()
    {
        var sessionId = Guid.NewGuid();

        await Run<SquidDbContext>(async db =>
        {
            db.Set<OctopusImportSession>().Add(CreateSession(sessionId, OctopusImportSessionState.Validated));
            await db.SaveChangesAsync();
        });

        var admissions = await Task.WhenAll(
            Run<IOctopusImportSessionService, bool>(service => service.TryStartConfirmationAsync(sessionId, 7)),
            Run<IOctopusImportSessionService, bool>(service => service.TryStartConfirmationAsync(sessionId, 7)));

        admissions.Count(admitted => admitted).ShouldBe(1);

        await Run<SquidDbContext>(async db =>
        {
            var session = await db.Set<OctopusImportSession>()
                .AsNoTracking()
                .SingleAsync(s => s.SessionId == sessionId);
            session.State.ShouldBe(OctopusImportSessionState.Importing.ToString());
        });
    }

    [Fact]
    public async Task ConfirmAsync_WhenTwoCallersRace_ImportsResourceExactlyOnce()
    {
        var sessionId = Guid.NewGuid();

        await Run<SquidDbContext>(async db =>
        {
            db.Set<OctopusImportSession>().Add(CreateSession(sessionId, OctopusImportSessionState.Validated));
            await db.SaveChangesAsync();
        });

        var confirmations = await Task.WhenAll(
            Run<IOctopusImportConfirmationOrchestrator, OctopusImportSessionDto>(orchestrator =>
                orchestrator.ConfirmAsync(CreateProjectGroupConfirmationRequest(sessionId, "Concurrent Imported Group"))),
            Run<IOctopusImportConfirmationOrchestrator, OctopusImportSessionDto>(orchestrator =>
                orchestrator.ConfirmAsync(CreateProjectGroupConfirmationRequest(sessionId, "Concurrent Imported Group"))));

        confirmations.ShouldAllBe(session =>
            session.State == OctopusImportSessionState.Importing ||
            session.State == OctopusImportSessionState.Succeeded);

        await Run<SquidDbContext>(async db =>
        {
            (await db.Set<ProjectGroup>()
                .CountAsync(group => group.SpaceId == 7 && group.Name == "Concurrent Imported Group"))
                .ShouldBe(1);

            var session = await db.Set<OctopusImportSession>()
                .AsNoTracking()
                .SingleAsync(candidate => candidate.SessionId == sessionId);
            session.State.ShouldBe(OctopusImportSessionState.Succeeded.ToString());
        });
    }

    [Fact]
    public async Task MarkInterruptedImportsFailedAsync_WhenImportIsStaleButSessionHasNotExpired_MarksFailed()
    {
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await Run<SquidDbContext>(async db =>
        {
            var session = CreateSession(sessionId, OctopusImportSessionState.Importing);
            session.ExpiresAt = now.AddHours(18);
            session.LastStateChangedAt = now.AddHours(-7);
            db.Set<OctopusImportSession>().Add(session);
            await db.SaveChangesAsync();
        });

        await Run<IOctopusImportSessionDataProvider>(async provider =>
        {
            var affected = await provider.MarkInterruptedImportsFailedAsync(
                now,
                now.AddHours(-6),
                TimeSpan.FromHours(24));

            affected.ShouldBe(1);
        });

        await Run<SquidDbContext>(async db =>
        {
            var session = await db.Set<OctopusImportSession>()
                .AsNoTracking()
                .SingleAsync(s => s.SessionId == sessionId);
            session.State.ShouldBe(OctopusImportSessionState.Failed.ToString());
            session.CompletedAt.ShouldNotBeNull();
            session.TemporaryUploadCleanupAfter.ShouldNotBeNull();
            session.TemporaryUploadCleanupAfter.Value.ShouldBeInRange(
                now.AddHours(24).AddMilliseconds(-1),
                now.AddHours(24).AddMilliseconds(1));
        });
    }

    [Fact]
    public async Task ConfirmAsync_WhenSessionIsExpired_ReturnsExpiredWithoutWritingResources()
    {
        var sessionId = Guid.NewGuid();

        await Run<SquidDbContext, IOctopusImportConfirmationOrchestrator>(async (db, orchestrator) =>
        {
            db.Set<OctopusImportSession>().Add(CreateSession(sessionId, OctopusImportSessionState.Expired));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var result = await orchestrator.ConfirmAsync(new OctopusImportConfirmationRequest(
                sessionId,
                7,
                new OctopusResourceGraph([], [], [], []),
                new OctopusImportDependencyPlan([], [], [], []),
                new OctopusImportPreviewPlanDto { GeneratedAt = DateTimeOffset.UtcNow }));

            result.State.ShouldBe(OctopusImportSessionState.Expired);
            result.Result.ShouldBeNull();
            (await db.Set<ProjectGroup>().CountAsync()).ShouldBe(0);
        });
    }

    [Fact]
    public async Task GetSessionAsync_WhenSessionBelongsToAnotherUser_ThrowsNotFound()
    {
        var sessionId = Guid.NewGuid();

        await Run<SquidDbContext>(async db =>
        {
            db.Set<OctopusImportSession>().Add(CreateSession(sessionId, OctopusImportSessionState.Validated));
            await db.SaveChangesAsync();
        });

        await Run<IOctopusImportSessionService>(
            async service =>
            {
                await Should.ThrowAsync<OctopusImportSessionNotFoundException>(
                    () => service.GetSessionAsync(sessionId, 7));
            },
            builder => builder.RegisterInstance<ICurrentUser>(new FixedCurrentUser(99)).SingleInstance());
    }

    private static OctopusImportSession CreateSession(Guid sessionId, OctopusImportSessionState state)
    {
        var now = DateTimeOffset.UtcNow;
        return new OctopusImportSession
        {
            SessionId = sessionId,
            DestinationSpaceId = 7,
            OwnerUserId = CurrentUsers.InternalUser.Id,
            State = state.ToString(),
            SourceSummaryJson = "{}",
            DataVersion = Guid.NewGuid().ToByteArray(),
            ExpiresAt = state == OctopusImportSessionState.Expired ? now.AddMinutes(-1) : now.AddHours(1),
            LastStateChangedAt = now,
            CompletedAt = state == OctopusImportSessionState.Expired ? now : null
        };
    }

    private static OctopusImportResourceResultDto CreatePreviewResource(OctopusResourceNode resource)
    {
        return new OctopusImportResourceResultDto
        {
            SourceId = resource.SourceId,
            SourceType = resource.Kind.ToString(),
            SourceName = resource.Name,
            PreviewAction = OctopusImportPreviewAction.Create,
            OutcomeState = OctopusImportResourceOutcomeState.Pending
        };
    }

    private static OctopusImportConfirmationRequest CreateProjectGroupConfirmationRequest(Guid sessionId, string name)
    {
        var resource = new OctopusResourceNode(
            "ProjectGroups-concurrent",
            name,
            OctopusResourceKind.ProjectGroup,
            OctopusDocumentKind.ProjectGroup,
            "ProjectGroups-concurrent.json",
            null,
            null,
            false,
            new OctopusProjectGroupDto
            {
                Id = "ProjectGroups-concurrent",
                Name = name,
                Slug = "concurrent-imported-group"
            });

        return new OctopusImportConfirmationRequest(
            sessionId,
            7,
            new OctopusResourceGraph([resource], [], [], []),
            new OctopusImportDependencyPlan([resource], [], [], []),
            new OctopusImportPreviewPlanDto
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                Resources = [CreatePreviewResource(resource)]
            });
    }

    private static string CreateArchive(params (string Id, string DocumentType, string DocumentSource, string Json)[] documents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"squid-octopus-import-{Guid.NewGuid():N}.zip");
        var manifest = JsonSerializer.Serialize(new
        {
            SchemaVersions = Array.Empty<string>(),
            Entries = documents.Select(document => new
            {
                document.Id,
                Name = document.Id,
                document.DocumentType,
                ExportType = "FullDocument",
                document.DocumentSource,
                ParentId = (string)null,
                Hash = Sha1(document.Json)
            })
        });

        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        WriteArchiveEntry(archive, "manifest.json", manifest);
        foreach (var document in documents)
            WriteArchiveEntry(archive, document.DocumentSource, document.Json);

        return path;
    }

    private static void WriteArchiveEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    private static string Sha1(string value)
    {
        var hash = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class FixedCurrentUser(int id) : ICurrentUser
    {
        public int? Id => id;
        public string Name => "octopus-import-integration-test-user";
        public bool IsInternal => false;
    }
}
