using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Events;
using Squid.Message.Enums.Events;
using Squid.Message.Requests.Events;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.IntegrationTests.Services.Events;

/// <summary>
/// Drives the real <c>SquidDbContext.SaveChangesAsync</c> document-audit interceptor against
/// a real database: persisting / modifying / deleting a registered document emits the
/// matching DocumentCreated/Modified/Deleted event with the document's feed keys + name,
/// and an unregistered entity emits nothing. Best-effort by design — the originating change
/// is always committed regardless of the audit write.
/// </summary>
[Collection("EventsAudit")]
public class DocumentAuditInterceptorTests : TestBase
{
    public DocumentAuditInterceptorTests()
        : base("Events", "squid_it_doc_audit")
    {
    }

    private static ProjectGroup Group(string name, int spaceId) => new() { Name = name, Slug = name.ToLowerInvariant(), Description = "", SpaceId = spaceId };

    private static Environment EnvironmentDoc(string name, int spaceId) => new() { Name = name, Slug = name.ToLowerInvariant(), Description = "", SpaceId = spaceId };

    private static async Task<List<EventDto>> DocumentEventsAsync(IEventService events, int spaceId)
    {
        var page = await events.GetEventsAsync(new GetEventsRequest { SpaceId = spaceId, Take = 100 });

        return page.Events;
    }

    [Fact]
    public async Task InsertingADocument_RecordsDocumentCreated_WithFeedKeysAndName()
    {
        const int spaceId = 800;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            await repository.InsertAsync(Group("Payments", spaceId));
            await unitOfWork.SaveChangesAsync();
        }).ConfigureAwait(false);

        await Run<IEventService>(async events =>
        {
            var created = (await DocumentEventsAsync(events, spaceId)).Where(e => e.Category == (int)EventCategory.DocumentCreated).ToList();

            created.Count.ShouldBe(1);
            created[0].ReferencesJson.ShouldContain("ProjectGroup");
            created[0].ReferencesJson.ShouldContain("Payments");
            created[0].Username.ShouldBe("system");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task ModifyingADocument_RecordsDocumentModified()
    {
        const int spaceId = 801;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var group = Group("Original", spaceId);
            await repository.InsertAsync(group);
            await unitOfWork.SaveChangesAsync();   // DocumentCreated

            group.Name = "Renamed";
            await unitOfWork.SaveChangesAsync();   // DocumentModified
        }).ConfigureAwait(false);

        await Run<IEventService>(async events =>
        {
            var categories = (await DocumentEventsAsync(events, spaceId)).Select(e => e.Category).ToList();

            categories.Count(c => c == (int)EventCategory.DocumentCreated).ShouldBe(1);
            categories.Count(c => c == (int)EventCategory.DocumentModified).ShouldBe(1);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task DeletingADocument_RecordsDocumentDeleted()
    {
        const int spaceId = 802;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var group = Group("Doomed", spaceId);
            await repository.InsertAsync(group);
            await unitOfWork.SaveChangesAsync();

            await repository.DeleteAsync(group);
            await unitOfWork.SaveChangesAsync();   // DocumentDeleted (entity object keeps its id post-delete)
        }).ConfigureAwait(false);

        await Run<IEventService>(async events =>
        {
            (await DocumentEventsAsync(events, spaceId)).Count(e => e.Category == (int)EventCategory.DocumentDeleted).ShouldBe(1);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SavingTwoDocumentsInOneSave_RecordsOneEventEach_OnTheirOwnFeeds()
    {
        const int spaceId = 803;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            await repository.InsertAsync(Group("Group", spaceId));
            await repository.InsertAsync(EnvironmentDoc("Staging", spaceId));
            await unitOfWork.SaveChangesAsync();
        }).ConfigureAwait(false);

        await Run<IEventService>(async events =>
        {
            var created = (await DocumentEventsAsync(events, spaceId)).Where(e => e.Category == (int)EventCategory.DocumentCreated).ToList();

            created.Count.ShouldBe(2, "each document in a multi-entity save gets its own audit event — and nothing else does");
            created.ShouldContain(e => e.ReferencesJson.Contains("ProjectGroup"));

            var environmentEvent = created.Single(e => e.ReferencesJson.Contains("Environment"));
            environmentEvent.EnvironmentId.ShouldNotBeNull("an environment audit event surfaces on the environment feed");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SavingAnUnregisteredEntity_RecordsNothing()
    {
        const int spaceId = 804;

        // ServerTask is persisted but is NOT a user-facing document → not in the registry →
        // no audit noise. Guards against "audit every entity / every IAuditable" creep.
        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            await repository.InsertAsync(new ServerTask
            {
                Name = "unaudited-task",
                Description = "",
                ErrorMessage = "",
                ConcurrencyTag = "",
                State = "Pending",
                JSON = "{}",
                BusinessProcessState = "",
                ServerTaskType = "Deploy",
                JobId = "",
                DataVersion = Array.Empty<byte>(),
                SpaceId = spaceId
            });
            await unitOfWork.SaveChangesAsync();
        }).ConfigureAwait(false);

        await Run<IEventService>(async events =>
        {
            (await DocumentEventsAsync(events, spaceId)).ShouldBeEmpty("non-document entities must not enter the audit stream");
        }).ConfigureAwait(false);
    }
}
