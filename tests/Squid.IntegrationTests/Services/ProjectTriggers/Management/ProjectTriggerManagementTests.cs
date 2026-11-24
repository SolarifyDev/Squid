using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Moq;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization.Exceptions;
using Squid.Core.Services.Identity;
using Squid.Core.Services.ProjectTriggers;
using Squid.Core.Services.ProjectTriggers.Management;
using Squid.Message.Commands.ProjectTriggers;
using Squid.Message.Enums;
using Squid.Message.Models.ProjectTriggers;
using Squid.Message.Requests.ProjectTriggers;

namespace Squid.IntegrationTests.Services.ProjectTriggers.Management;

public class ProjectTriggerManagementTests() : TestBase("ProjectTriggerManagement", "squid_it_trigger_management")
{
    private const string TriggerType = ProjectTriggerTypes.ExternalFeed;
    private const string ActionType = ProjectTriggerActions.CreateRelease;
    private const string Payload = "{\"schemaVersion\":1,\"sourceId\":456}";
    private static readonly ProjectTriggerCapability Capability = new(TriggerType, ActionType);
    private readonly Mock<IProjectTriggerHandler> _handler = new();
    private readonly Mock<IProjectTriggerRegistry> _registry = new();
    private int _actor;
    private int _project;
    private int _otherProject;
    private int _role;
    private int _space;

    [Fact]
    public async Task MediatorCreatesRegisteredDefinition_ReadsConfigurationAndOnlyRegisteredTypes()
    {
        await SeedAsync();
        var created = await CreateAsync();

        var dto = (await Run<IMediator, GetProjectTriggerResponse>(m => m.RequestAsync<GetProjectTriggerRequest, GetProjectTriggerResponse>(
            new GetProjectTriggerRequest { ProjectId = _project, TriggerId = created.Id }), RegisterCaller)).Data;
        dto.Configuration.Value.GetProperty("sourceId").GetInt32().ShouldBe(456);
        dto.SpaceId.ShouldBe(_space);
        dto.ExecutionUserId.ShouldBe(_actor);
        dto.Revision.ShouldBe(1);
        dto.IsAvailable.ShouldBeTrue();

        var types = (await Run<IMediator, GetProjectTriggerTypesResponse>(m => m.RequestAsync<GetProjectTriggerTypesRequest, GetProjectTriggerTypesResponse>(
            new GetProjectTriggerTypesRequest { ProjectId = _project }), RegisterCaller)).Data;
        types.ShouldBe([Capability]);
    }

    [Fact]
    public async Task AnotherProjectOrSpaceCannotInspectHistoryOrChangeTrigger()
    {
        await SeedAsync();
        var created = await CreateAsync();

        await Should.ThrowAsync<ProjectTriggerNotFoundException>(() => Management(s => s.DetailAsync(new()
            { ProjectId = _otherProject, TriggerId = created.Id }, default)));
        await Should.ThrowAsync<ProjectTriggerNotFoundException>(() => Management(s => s.HistoryAsync(new()
            { ProjectId = _otherProject, TriggerId = created.Id }, default)));
        await Should.ThrowAsync<ProjectTriggerNotFoundException>(() => Management(s => s.DisableAsync(new()
            { ProjectId = _otherProject, TriggerId = created.Id, ExpectedRevision = 1 }, default)));
        await Should.ThrowAsync<ProjectTriggerNotFoundException>(() => Management(s => s.ListAsync(new()
            { ProjectId = _project, SpaceId = _space + 1 }, default)));

        (await DetailAsync(created.Id)).Revision.ShouldBe(1);
    }

    [Fact]
    public async Task ProjectGroupRestrictionAndRevokedActionDenyWithoutSideEffects()
    {
        await SeedAsync();
        await Run<SquidDbContext>(async db =>
        {
            var scoped = await db.Set<ScopedUserRole>().SingleAsync(r => r.UserRoleId == _role);
            db.Add(new ScopedUserRoleProjectGroup { ScopedUserRoleId = scoped.Id, ProjectGroupId = 999 });
            await db.SaveChangesAsync();
        });
        await Should.ThrowAsync<PermissionDeniedException>(() => Management(s => s.ListAsync(new() { ProjectId = _project }, default)));
        await Should.ThrowAsync<PermissionDeniedException>(() => CreateAsync());
        await Run<SquidDbContext>(async db =>
        {
            await db.Set<ScopedUserRoleProjectGroup>().ExecuteDeleteAsync();
            await db.Set<UserRolePermission>().Where(p => p.UserRoleId == _role && p.Permission == nameof(Permission.ReleaseCreate)).ExecuteDeleteAsync();
        });
        (await Should.ThrowAsync<PermissionDeniedException>(() => CreateAsync())).Permission.ShouldBe(Permission.ReleaseCreate);
        await Run<SquidDbContext>(async db => (await db.Set<ProjectTrigger>().CountAsync()).ShouldBe(0));
    }

    [Fact]
    public async Task UnknownHandlerKeepsMetadataAndHistoryReadableWithoutExposingOpaquePayloads()
    {
        await SeedAsync();
        var created = await CreateAsync(isDisabled: true);
        await AddHistoryAsync(created.Id);
        _registry.SetupGet(r => r.Capabilities).Returns([]);
        _registry.Setup(r => r.Resolve(TriggerType, ActionType)).Returns((IProjectTriggerHandler)null);

        var detail = await DetailAsync(created.Id);
        detail.IsAvailable.ShouldBeFalse();
        detail.Configuration.ShouldBeNull();
        (await Should.ThrowAsync<ValidationException>(() => Management(s => s.EnableAsync(new()
            { ProjectId = _project, TriggerId = created.Id, ExpectedRevision = 1 }, default)))).Message.ShouldContain("available");
        var history = await HistoryAsync(created.Id);
        history.Items.Single().Reason.ShouldBe("Source observed.");
        var json = JsonSerializer.Serialize(history);
        json.ShouldNotContain("private-material");
        json.ShouldNotContain("OccurrenceKey");
        json.ShouldNotContain("SourceKey");
        var page = await Management(s => s.ListAsync(new() { ProjectId = _project }, default));
        page.Items.Single().IsAvailable.ShouldBeFalse();
        page.Items.Single().Revision.ShouldBe(1);

        // Register a different working type. Switching away from an unknown stored type may discard opaque data.
        _registry.SetupGet(r => r.Capabilities).Returns([new(ProjectTriggerTypes.GitRepository, ActionType)]);
        var replacement = Update(created.Id, 1, Payload);
        replacement.TriggerType = ProjectTriggerTypes.GitRepository;
        await Should.ThrowAsync<ValidationException>(() => Management(s => s.UpdateAsync(replacement, default)));
        var deleted = await Management(s => s.DeleteAsync(new() { ProjectId = _project, TriggerId = created.Id, ExpectedRevision = 1 }, default));
        deleted.IsDeleted.ShouldBeTrue();
        (await HistoryAsync(created.Id)).Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteredTypeWithOldPayloadCanBeRepairedWithoutDisclosingIt(bool deeplyNested)
    {
        await SeedAsync();
        var created = await CreateAsync(isDisabled: true);
        var oldPayload = deeplyNested
            ? "{\"legacy\":" + new string('[', 75) + "\"private-material\"" + new string(']', 75) + "}"
            : "{\"schemaVersion\":2,\"legacy\":\"private-material\"}";
        await Run<SquidDbContext>(db => db.Set<ProjectTrigger>().Where(t => t.Id == created.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.ConfigurationJson, oldPayload)));

        var old = await DetailAsync(created.Id);
        old.Configuration.ShouldBeNull();
        var repaired = await Management(s => s.UpdateAsync(Update(created.Id, 1, Payload), default));

        repaired.Revision.ShouldBe(2);
        (await DetailAsync(created.Id)).Configuration.Value.GetProperty("sourceId").GetInt32().ShouldBe(456);
    }

    [Fact]
    public async Task HandlerValidationAndScopeAreRequiredEvenForDisabledConfiguration()
    {
        await SeedAsync();
        const string secret = "private-material";
        _handler.Setup(h => h.ValidateAsync(It.IsAny<ProjectTriggerDefinition>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException(secret));

        var invalid = await Should.ThrowAsync<ValidationException>(() => CreateAsync(isDisabled: true));
        invalid.ToString().ShouldNotContain(secret);
        _handler.Setup(h => h.ValidateAsync(It.IsAny<ProjectTriggerDefinition>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException(secret));
        var missing = await Should.ThrowAsync<ValidationException>(() => CreateAsync(isDisabled: true));
        missing.ToString().ShouldNotContain(secret);
        _handler.Setup(h => h.ValidateAsync(It.IsAny<ProjectTriggerDefinition>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TriggerResourceScope(9999, null, null));
        await Should.ThrowAsync<ValidationException>(() => CreateAsync(isDisabled: true));
        await Run<SquidDbContext>(async db => (await db.Set<ProjectTrigger>().CountAsync()).ShouldBe(0));
    }

    [Fact]
    public async Task DescriptionEditPreservesIdentityAndCursor_ExecutionEditAndReenableRebindAndClearState()
    {
        await SeedAsync();
        var created = await CreateAsync();
        await AddHistoryAsync(created.Id);
        var owner = _actor;
        _actor = await AddActorAsync("second");

        var metadata = await Management(s => s.UpdateAsync(Update(created.Id, 1, "{ \"sourceId\":456, \"schemaVersion\":1 }"), default));
        metadata.ExecutionUserId.ShouldBe(owner);
        metadata.Revision.ShouldBe(2);
        (await ExecutionRevisionAsync(created.Id)).ShouldBe(1);
        (await DetailAsync(created.Id)).LastOutcome.ShouldBe("Checked.");
        var changed = await Management(s => s.UpdateAsync(Update(created.Id, 2, "{\"schemaVersion\":1,\"sourceId\":789}"), default));
        changed.ExecutionUserId.ShouldBe(_actor);
        (await ExecutionRevisionAsync(created.Id)).ShouldBe(3);
        (await DetailAsync(created.Id)).LastCheckedAt.ShouldBeNull();
        (await HistoryAsync(created.Id)).Count.ShouldBe(1);

        await AddStateAsync(created.Id);
        await Management(s => s.DisableAsync(new() { ProjectId = _project, TriggerId = created.Id, ExpectedRevision = 3 }, default));
        _actor = owner;
        var enabled = await Management(s => s.EnableAsync(new() { ProjectId = _project, TriggerId = created.Id, ExpectedRevision = 4 }, default));
        enabled.ExecutionUserId.ShouldBe(owner);
        enabled.IsDisabled.ShouldBeFalse();
        (await ExecutionRevisionAsync(created.Id)).ShouldBe(5);
        (await DetailAsync(created.Id)).LastCheckedAt.ShouldBeNull();
        (await HistoryAsync(created.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task DisablingAndDeletingKeepRuns_HideTombstoneAndRejectStaleMutations()
    {
        await SeedAsync();
        var created = await CreateAsync();
        await AddHistoryAsync(created.Id);
        var disabled = await Management(s => s.DisableAsync(new() { ProjectId = _project, TriggerId = created.Id, ExpectedRevision = 1 }, default));
        disabled.Revision.ShouldBe(2);
        (await HistoryAsync(created.Id)).Items.Single().Revision.ShouldBe(1);
        await Should.ThrowAsync<ProjectTriggerRevisionConflictException>(() => Management(s => s.DeleteAsync(new()
            { ProjectId = _project, TriggerId = created.Id, ExpectedRevision = 1 }, default)));
        var deleted = await Management(s => s.DeleteAsync(new() { ProjectId = _project, TriggerId = created.Id, ExpectedRevision = 2 }, default));
        deleted.IsDeleted.ShouldBeTrue();
        (await Management(s => s.ListAsync(new() { ProjectId = _project }, default))).Count.ShouldBe(0);
        await Should.ThrowAsync<ProjectTriggerNotFoundException>(() => DetailAsync(created.Id));
        var run = (await HistoryAsync(created.Id)).Items.Single();
        run.TriggerId.ShouldBe(created.Id);
        run.ExecutionUserId.ShouldBe(_actor);
    }

    [Fact]
    public async Task ConcurrentUpdatesWithSameExpectedRevision_OnlyOneSaves()
    {
        await SeedAsync();
        var created = await CreateAsync();
        var arrived = 0;
        var bothRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.Setup(h => h.ValidateAsync(It.IsAny<ProjectTriggerDefinition>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                if (Interlocked.Increment(ref arrived) == 2) bothRead.TrySetResult();
                await bothRead.Task.WaitAsync(TimeSpan.FromSeconds(15));
                return new TriggerResourceScope(null, null, null);
            });

        async Task<Exception> TryUpdate(string config) => await Record.ExceptionAsync(() =>
            Management(s => s.UpdateAsync(Update(created.Id, 1, config), default)));

        var errors = await Task.WhenAll(TryUpdate("{\"schemaVersion\":1,\"sourceId\":1}"), TryUpdate("{\"schemaVersion\":1,\"sourceId\":2}"));
        errors.Count(e => e == null).ShouldBe(1);
        errors.Single(e => e != null).ShouldBeOfType<ProjectTriggerRevisionConflictException>();
        (await DetailAsync(created.Id)).Revision.ShouldBe(2);
    }

    [Fact]
    public async Task ListAndHistoryPagesAreStableAndShowLatestCheckWithEarliestDueTime()
    {
        await SeedAsync();
        var first = await CreateAsync();
        var second = await CreateAsync();
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        now = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
        await Run<SquidDbContext>(async db =>
        {
            db.AddRange(new ProjectTriggerState { TriggerId = first.Id, SourceKey = "a", LastCheckedAt = now, LastOutcome = "Earlier check.", NextDueAt = now.AddHours(1) },
                new ProjectTriggerState { TriggerId = first.Id, SourceKey = "b", LastCheckedAt = now.AddMinutes(1), LastOutcome = "Latest check.", NextDueAt = now.AddHours(2) });
            for (var i = 0; i < 3; i++)
            {
                db.Add(new ProjectTriggerRun { TriggerId = first.Id, Revision = 1, ExecutionUserId = _actor, ObservedAt = now,
                    NextAttemptAt = now, Status = "Skipped", Reason = $"Skip {i}", SourceKey = "source", OccurrenceKey = $"item-{i}",
                    ConfigurationJson = Payload, EvidenceJson = "{}", TriggerType = TriggerType, ActionType = ActionType });
            }
            await db.SaveChangesAsync();
        });

        var page = await Management(s => s.ListAsync(new() { ProjectId = _project, PageSize = 1 }, default));
        page.Count.ShouldBe(2);
        var summary = page.Items.Single();
        summary.Id.ShouldBe(first.Id);
        summary.LastCheckedAt.ShouldBe(now.AddMinutes(1));
        summary.LastOutcome.ShouldBe("Latest check.");
        summary.NextDueAt.ShouldBe(now.AddHours(1));
        var nextPage = await Management(s => s.ListAsync(new() { ProjectId = _project, PageSize = 1, PageIndex = 2 }, default));
        nextPage.Items.Single().Id.ShouldBe(second.Id);

        var runs = await HistoryAsync(first.Id);
        runs.Count.ShouldBe(3);
        var ids = runs.Items.Select(r => r.Id).ToList();
        ids.ShouldBe(ids.OrderByDescending(id => id).ToList());
        var middle = await Management(s => s.HistoryAsync(new() { ProjectId = _project, TriggerId = first.Id, PageIndex = 2, PageSize = 1 }, default));
        middle.Items.Single().Id.ShouldBe(ids[1]);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    [InlineData(int.MaxValue, 100)]
    public async Task ReadPagesRejectInvalidOrOverflowingRanges(int index, int size)
    {
        await SeedAsync();
        var trigger = await CreateAsync();
        await Should.ThrowAsync<ValidationException>(() => Management(s => s.ListAsync(new()
            { ProjectId = _project, PageIndex = index, PageSize = size }, default)));
        await Should.ThrowAsync<ValidationException>(() => Management(s => s.HistoryAsync(new()
            { ProjectId = _project, TriggerId = trigger.Id, PageIndex = index, PageSize = size }, default)));
    }

    private Task<T> Management<T>(Func<IProjectTriggerManagementService, Task<T>> test) =>
        Run<IProjectTriggerManagementService, T>(test, RegisterCaller);

    private Task<int> ExecutionRevisionAsync(int triggerId) => Run<SquidDbContext, int>(db =>
        db.Set<ProjectTrigger>().Where(t => t.Id == triggerId).Select(t => t.ExecutionRevision).SingleAsync());

    private Task<ProjectTriggerDto> DetailAsync(int triggerId) => Management(s => s.DetailAsync(new()
        { ProjectId = _project, TriggerId = triggerId }, default));

    private Task<ProjectTriggerPage<ProjectTriggerRunDto>> HistoryAsync(int triggerId) => Management(s => s.HistoryAsync(new()
        { ProjectId = _project, TriggerId = triggerId }, default));

    private async Task<ProjectTriggerChangeDto> CreateAsync(bool isDisabled = false) =>
        (await Run<IMediator, CreateProjectTriggerResponse>(m => m.SendAsync<CreateProjectTriggerCommand, CreateProjectTriggerResponse>(new()
        {
            ProjectId = _project, Name = "Monitor", TriggerType = TriggerType, ActionType = ActionType,
            Configuration = JsonSerializer.Deserialize<JsonElement>(Payload), IsDisabled = isDisabled
        }), RegisterCaller)).Data;

    private UpdateProjectTriggerCommand Update(int id, int revision, string config) => new()
    {
        ProjectId = _project, TriggerId = id, ExpectedRevision = revision, Name = "Monitor", Description = "Edited",
        TriggerType = TriggerType, ActionType = ActionType, Configuration = JsonSerializer.Deserialize<JsonElement>(config)
    };

    private void RegisterCaller(ContainerBuilder builder)
    {
        builder.RegisterInstance(Mock.Of<ICurrentUser>(u => u.Id == _actor && !u.IsInternal)).As<ICurrentUser>();
        builder.RegisterInstance(_registry.Object).As<IProjectTriggerRegistry>();
    }

    private async Task SeedAsync()
    {
        _handler.Setup(h => h.ValidateAsync(It.IsAny<ProjectTriggerDefinition>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TriggerResourceScope(null, null, null));
        _registry.SetupGet(r => r.Capabilities).Returns([Capability]);
        _registry.Setup(r => r.Resolve(TriggerType, ActionType)).Returns(_handler.Object);
        await Run<SquidDbContext>(async db =>
        {
            var space = new Space { Name = "Triggers", Slug = "triggers" };
            db.Add(space);
            await db.SaveChangesAsync();
            _space = space.Id;
            var project = new Project { Name = "Project", Slug = "project", SpaceId = _space, Json = "{}", IncludedLibraryVariableSetIds = "[]", ProjectGroupId = 12 };
            var other = new Project { Name = "Other", Slug = "other", SpaceId = _space, Json = "{}", IncludedLibraryVariableSetIds = "[]", ProjectGroupId = 12 };
            var role = new UserRole { Name = "Trigger role" };
            db.AddRange(project, other, role);
            await db.SaveChangesAsync();
            _project = project.Id;
            _otherProject = other.Id;
            _role = role.Id;
            foreach (var permission in new[] { Permission.ProjectEdit, Permission.ProjectView, Permission.ReleaseCreate })
                db.Add(new UserRolePermission { UserRoleId = _role, Permission = permission.ToString() });
            await db.SaveChangesAsync();
        });
        _actor = await AddActorAsync("first");
    }

    private async Task<int> AddActorAsync(string name) => await Run<SquidDbContext, int>(async db =>
    {
        var user = new UserAccount { UserName = name, NormalizedUserName = name.ToUpperInvariant(), DisplayName = name, PasswordHash = "" };
        var team = new Team { Name = name, SpaceId = _space };
        db.AddRange(user, team);
        await db.SaveChangesAsync();
        db.AddRange(new TeamMember { TeamId = team.Id, UserId = user.Id }, new ScopedUserRole { UserRoleId = _role, TeamId = team.Id, SpaceId = _space });
        await db.SaveChangesAsync();
        return user.Id;
    });

    private async Task AddHistoryAsync(int triggerId)
    {
        await Run<SquidDbContext>(async db =>
        {
            db.Add(new ProjectTriggerRun
            {
                TriggerId = triggerId, TriggerType = TriggerType, ActionType = ActionType, Revision = 1, ExecutionUserId = _actor,
                ObservedAt = DateTimeOffset.UtcNow, NextAttemptAt = DateTimeOffset.UtcNow, Status = "Skipped", Reason = "Source observed.",
                ConfigurationJson = "{\"schemaVersion\":1,\"private\":\"private-material\"}",
                OccurrenceKey = "private-material", SourceKey = "private-material", EvidenceJson = "{\"data\":\"private-material\"}",
                ActionPlanJson = "{\"data\":\"private-material\"}"
            });
            await db.SaveChangesAsync();
        });
        await AddStateAsync(triggerId);
    }

    private Task AddStateAsync(int triggerId) => Run<SquidDbContext>(async db =>
    {
        db.Add(new ProjectTriggerState { TriggerId = triggerId, SourceKey = "source", Cursor = "private-material", LastCheckedAt = DateTimeOffset.UtcNow, LastOutcome = "Checked." });
        await db.SaveChangesAsync();
    });
}
