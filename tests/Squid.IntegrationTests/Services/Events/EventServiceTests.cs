using Squid.Core.Persistence.Db;
using Squid.Core.Services.Events;
using Squid.Message.Enums.Events;
using Squid.Message.Models.Events;
using Squid.Message.Requests.Events;

namespace Squid.IntegrationTests.Services.Events;

public class EventServiceTests : TestBase
{
    public EventServiceTests()
        : base("Events", "squid_it_events")
    {
    }

    [Fact]
    public async Task RecordAsync_PersistsEvent_AndGetEventsReturnsItFilteredByDocument()
    {
        const int spaceId = 1;
        const int releaseId = 4242;

        await Run<IEventService, IUnitOfWork>(async (events, uow) =>
        {
            await events.RecordAsync(new RecordEventRequest
            {
                Category = EventCategory.DeploymentSucceeded,
                SpaceId = spaceId,
                ReleaseId = releaseId,
                ProjectId = 7,
                EnvironmentId = 3,
                References = new { Release = new { name = "6.6.2" }, Environment = new { id = 3, name = "PRD" } }
            });

            await uow.SaveChangesAsync();
        }).ConfigureAwait(false);

        await Run<IEventService>(async events =>
        {
            var page = await events.GetEventsAsync(new GetEventsRequest { SpaceId = spaceId, ReleaseId = releaseId });

            page.Events.Count.ShouldBe(1);

            var dto = page.Events[0];
            dto.Category.ShouldBe((int)EventCategory.DeploymentSucceeded);
            dto.CategoryName.ShouldBe("Deployment succeeded");
            dto.MessageTemplate.ShouldContain("{Environment}");
            dto.ReleaseId.ShouldBe(releaseId);
            dto.ProjectId.ShouldBe(7);
            dto.Username.ShouldBe("system");               // InternalUser context in integration tests
            dto.EstablishedWith.ShouldBe((int)EventIdentityEstablishedWith.Server);
            dto.ReferencesJson.ShouldContain("6.6.2");      // jsonb round-trips
            page.HasMore.ShouldBeFalse();
            page.NextCursor.ShouldBeNull();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task GetEventsAsync_KeysetPaginates_NewestFirst()
    {
        const int spaceId = 1;
        const int releaseId = 9001;

        await Run<IEventService, IUnitOfWork>(async (events, uow) =>
        {
            for (var i = 0; i < 5; i++)
                await events.RecordAsync(new RecordEventRequest { Category = EventCategory.DeploymentStarted, SpaceId = spaceId, ReleaseId = releaseId });

            await uow.SaveChangesAsync();
        }).ConfigureAwait(false);

        await Run<IEventService>(async events =>
        {
            var firstPage = await events.GetEventsAsync(new GetEventsRequest { SpaceId = spaceId, ReleaseId = releaseId, Take = 2 });

            firstPage.Events.Count.ShouldBe(2);
            firstPage.HasMore.ShouldBeTrue();
            firstPage.NextCursor.ShouldNotBeNull();
            firstPage.Events[0].Id.ShouldBeGreaterThan(firstPage.Events[1].Id, "newest first");

            var secondPage = await events.GetEventsAsync(new GetEventsRequest { SpaceId = spaceId, ReleaseId = releaseId, Take = 2, BeforeId = firstPage.NextCursor });

            secondPage.Events.Count.ShouldBe(2);
            secondPage.Events[0].Id.ShouldBeLessThan(firstPage.Events[1].Id, "cursor advanced to older rows");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task GetEventsAsync_IsolatesBySpaceAndDocument()
    {
        await Run<IEventService, IUnitOfWork>(async (events, uow) =>
        {
            await events.RecordAsync(new RecordEventRequest { Category = EventCategory.DeploymentQueued, SpaceId = 1, ReleaseId = 100 });
            await events.RecordAsync(new RecordEventRequest { Category = EventCategory.DeploymentQueued, SpaceId = 2, ReleaseId = 100 });
            await events.RecordAsync(new RecordEventRequest { Category = EventCategory.DeploymentQueued, SpaceId = 1, ReleaseId = 200 });

            await uow.SaveChangesAsync();
        }).ConfigureAwait(false);

        await Run<IEventService>(async events =>
        {
            var page = await events.GetEventsAsync(new GetEventsRequest { SpaceId = 1, ReleaseId = 100 });

            page.Events.Count.ShouldBe(1, "must not leak across space or release");
            page.Events[0].SpaceId.ShouldBe(1);
            page.Events[0].ReleaseId.ShouldBe(100);
        }).ConfigureAwait(false);
    }
}
