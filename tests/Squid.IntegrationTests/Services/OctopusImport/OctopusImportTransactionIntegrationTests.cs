using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.IntegrationTests.Services.OctopusImport;

public class OctopusImportTransactionIntegrationTests : TestBase
{
    public OctopusImportTransactionIntegrationTests()
        : base("OctopusImportTransaction", "squid_it_octopus_import_transaction")
    {
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
}
