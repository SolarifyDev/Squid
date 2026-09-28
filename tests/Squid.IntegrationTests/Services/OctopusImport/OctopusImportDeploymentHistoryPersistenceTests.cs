using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Persistence.Entities.Events;
using Squid.Core.Services.Deployments.LifeCycle;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Events;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Constants;
using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Enums.Deployments;
using Squid.Message.Enums.Events;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Requests.Events;
using SquidMessageLifecycle = Squid.Core.Persistence.Entities.Deployments.Lifecycle;

namespace Squid.IntegrationTests.Services.OctopusImport;

public class OctopusImportDeploymentHistoryPersistenceTests : TestBase
{
    private const int SpaceId = 7;
    private const string RealArchiveEnvironmentVariable = "SQUID_OCTOPUS_REAL_IMPORT_ARCHIVE";

    public OctopusImportDeploymentHistoryPersistenceTests()
        : base("OctopusImportDeploymentHistory", "squid_it_octopus_import_deployment_history")
    {
    }

    [Fact]
    public async Task ImportAsync_PreservesHistoryAndMatchesOrFallsBackOperatorIdentity()
    {
        var matchedUser = new UserAccount
        {
            UserName = "alice",
            NormalizedUserName = "ALICE",
            DisplayName = "Alice",
            PasswordHash = string.Empty,
            IsDisabled = false,
            IsSystem = false
        };

        await Run<SquidDbContext, IOctopusImportDeploymentHistoryImporter>(
            async (db, importer) =>
            {
                db.Set<UserAccount>().Add(matchedUser);
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();

                var project = new Project
                {
                    Name = "History project",
                    Slug = "history-project",
                    SpaceId = SpaceId,
                    IsDisabled = false,
                    Json = "{}",
                    IncludedLibraryVariableSetIds = "[]"
                };
                var environment = new Squid.Core.Persistence.Entities.Deployments.Environment
                {
                    Name = "Historical Production",
                    Slug = "historical-production",
                    SpaceId = SpaceId
                };
                db.Set<Project>().Add(project);
                db.Set<Squid.Core.Persistence.Entities.Deployments.Environment>().Add(environment);
                await db.SaveChangesAsync();

                var channel = new Channel
                {
                    Name = "Default",
                    Slug = "default",
                    ProjectId = project.Id,
                    SpaceId = SpaceId,
                    IsDefault = true
                };
                db.Set<Channel>().Add(channel);
                await db.SaveChangesAsync();

                var release = new Release
                {
                    Version = "1.0.0",
                    ProjectId = project.Id,
                    ChannelId = channel.Id,
                    SpaceId = SpaceId,
                    CreatedDate = DateTimeOffset.Parse("2024-07-01T00:00:00Z")
                };
                db.Set<Release>().Add(release);
                await db.SaveChangesAsync();

                var matched = await importer.ImportAsync(Request(
                    project.Id,
                    channel.Id,
                    release.Id,
                    environment.Id,
                    "Deploy to TEST",
                    "Alice",
                    "Users-1",
                    "Success",
                    DateTimeOffset.Parse("2024-07-10T09:47:02Z"),
                    DateTimeOffset.Parse("2024-07-10T09:48:11Z")));

                var unmatched = await importer.ImportAsync(Request(
                    project.Id,
                    channel.Id,
                    release.Id,
                    environment.Id,
                    "Deploy to PRD",
                    "levi.y",
                    "Users-608",
                    "Failed",
                    DateTimeOffset.Parse("2024-07-11T09:47:02Z"),
                    DateTimeOffset.Parse("2024-07-11T09:48:11Z")));

                var matchedDeployment = await db.Set<Deployment>()
                    .AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == matched.DeploymentId);
                var unmatchedDeployment = await db.Set<Deployment>()
                    .AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == unmatched.DeploymentId);
                var matchedTask = await db.Set<Squid.Core.Persistence.Entities.Deployments.ServerTask>()
                    .AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == matched.TaskId);
                var unmatchedTask = await db.Set<Squid.Core.Persistence.Entities.Deployments.ServerTask>()
                    .AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == unmatched.TaskId);
                var completions = await db.Set<DeploymentCompletion>()
                    .AsNoTracking()
                    .Where(candidate => candidate.DeploymentId == matched.DeploymentId || candidate.DeploymentId == unmatched.DeploymentId)
                    .ToListAsync();
                var logs = await db.Set<ServerTaskLog>()
                    .AsNoTracking()
                    .Where(candidate => candidate.ServerTaskId == matched.TaskId || candidate.ServerTaskId == unmatched.TaskId)
                    .OrderBy(candidate => candidate.ServerTaskId)
                    .ToListAsync();
                var events = await db.Set<Event>()
                    .AsNoTracking()
                    .Where(candidate =>
                        candidate.ReleaseId == release.Id
                        && (candidate.Category == EventCategory.DeploymentSucceeded
                            || candidate.Category == EventCategory.DeploymentFailed))
                    .OrderBy(candidate => candidate.Occurred)
                    .ToListAsync();

                matched.CreatedBy.ShouldBe(matchedUser.Id);
                matchedDeployment.CreatedBy.ShouldBe(matchedUser.Id);
                matchedDeployment.DeployedBy.ShouldBe(matchedUser.Id);
                matchedDeployment.DeployedToMachineIds.ShouldBe("""["Machines-1"]""");

                unmatched.CreatedBy.ShouldBe(CurrentUsers.InternalUser.Id);
                unmatchedDeployment.CreatedBy.ShouldBe(CurrentUsers.InternalUser.Id);
                unmatchedDeployment.DeployedBy.ShouldBe(CurrentUsers.InternalUser.Id);
                unmatchedDeployment.Json.ShouldContain("levi.y");
                unmatchedDeployment.Json.ShouldContain("Users-608");
                unmatchedTask.JSON.ShouldContain("levi.y");
                unmatchedTask.JSON.ShouldContain("Users-608");

                unmatchedTask.State.ShouldBe(TaskState.Failed);
                unmatchedTask.QueueTime.ShouldBe(DateTimeOffset.Parse("2024-07-11T09:47:01Z"));
                unmatchedTask.StartTime.ShouldBe(DateTimeOffset.Parse("2024-07-11T09:47:03Z"));
                unmatchedTask.CompletedTime.ShouldBe(DateTimeOffset.Parse("2024-07-11T09:48:11Z"));
                unmatchedTask.DurationSeconds.ShouldBe(68);

                completions.Count.ShouldBe(1);
                completions.Single().DeploymentId.ShouldBe(matched.DeploymentId);
                completions.Single().State.ShouldBe(TaskState.Success);
                completions.Single().CompletedTime.ShouldBe(DateTimeOffset.Parse("2024-07-10T09:48:11Z"));

                logs.Count.ShouldBe(2);
                logs.Single(log => log.ServerTaskId == unmatched.TaskId).Category.ShouldBe(ServerTaskLogCategory.Error);
                logs.Single(log => log.ServerTaskId == matched.TaskId).Detail.ShouldContain("Alice");

                events.Count.ShouldBe(2);
                events[0].Category.ShouldBe(EventCategory.DeploymentSucceeded);
                events[0].DeploymentId.ShouldBe(matched.DeploymentId);
                events[0].ServerTaskId.ShouldBe(matched.TaskId);
                events[0].ProjectId.ShouldBe(project.Id);
                events[0].ReleaseId.ShouldBe(release.Id);
                events[0].EnvironmentId.ShouldBe(environment.Id);
                events[0].UserId.ShouldBe(matchedUser.Id);
                events[0].Username.ShouldBe("Alice");
                events[0].Occurred.ShouldBe(DateTimeOffset.Parse("2024-07-10T09:48:11Z"));
                events[0].ReferencesJson.ShouldContain("1.0.0");
                events[0].ReferencesJson.ShouldContain("Historical Production");

                events[1].Category.ShouldBe(EventCategory.DeploymentFailed);
                events[1].DeploymentId.ShouldBe(unmatched.DeploymentId);
                events[1].ServerTaskId.ShouldBe(unmatched.TaskId);
                events[1].UserId.ShouldBeNull();
                events[1].Username.ShouldBe("levi.y");
                events[1].Occurred.ShouldBe(DateTimeOffset.Parse("2024-07-11T09:48:11Z"));
            });
    }

    [Fact]
    public async Task ImportAsync_HistoricalDeployments_AreVisibleInReleaseEventFeed()
    {
        const int spaceId = 17;

        await Run<SquidDbContext, IOctopusImportDeploymentHistoryImporter, IEventService>(
            async (db, importer, events) =>
            {
                var project = new Project
                {
                    Name = "History feed project",
                    Slug = "history-feed-project",
                    SpaceId = spaceId,
                    IsDisabled = false,
                    Json = "{}",
                    IncludedLibraryVariableSetIds = "[]"
                };
                var environment = new Squid.Core.Persistence.Entities.Deployments.Environment
                {
                    Name = "History feed production",
                    Slug = "history-feed-production",
                    SpaceId = spaceId
                };
                db.Set<Project>().Add(project);
                db.Set<Squid.Core.Persistence.Entities.Deployments.Environment>().Add(environment);
                await db.SaveChangesAsync();

                var channel = new Channel
                {
                    Name = "Default",
                    Slug = "default",
                    ProjectId = project.Id,
                    SpaceId = spaceId,
                    IsDefault = true
                };
                db.Set<Channel>().Add(channel);
                await db.SaveChangesAsync();

                var release = new Release
                {
                    Version = "2.0.0",
                    ProjectId = project.Id,
                    ChannelId = channel.Id,
                    SpaceId = spaceId,
                    CreatedDate = DateTimeOffset.Parse("2024-08-01T00:00:00Z")
                };
                db.Set<Release>().Add(release);
                await db.SaveChangesAsync();

                await importer.ImportAsync(Request(
                    project.Id,
                    channel.Id,
                    release.Id,
                    environment.Id,
                    "Deploy to PRD",
                    "levi.y",
                    "Users-608",
                    TaskState.Success,
                    DateTimeOffset.Parse("2024-08-10T09:47:02Z"),
                    DateTimeOffset.Parse("2024-08-10T09:48:11Z"),
                    spaceId));

                var page = await events.GetEventsAsync(new GetEventsRequest
                {
                    SpaceId = spaceId,
                    ReleaseId = release.Id,
                    Take = 100
                });

                var deploymentEvents = page.Events
                    .Where(candidate => candidate.Category == (int)EventCategory.DeploymentSucceeded)
                    .ToList();

                deploymentEvents.ShouldHaveSingleItem();
                deploymentEvents[0].Username.ShouldBe("levi.y");
                deploymentEvents[0].Occurred.ShouldBe(DateTimeOffset.Parse("2024-08-10T09:48:11Z"));
                deploymentEvents[0].ReferencesJson.ShouldContain("2.0.0");
                deploymentEvents[0].ReferencesJson.ShouldContain("History feed production");
            });
    }

    [Fact]
    public async Task CreateReleaseAsync_HistoricalCreatedDate_PreservesOctopusAssembledTimestamp()
    {
        await Run<SquidDbContext, IReleaseService, IRepository, IUnitOfWork>(
            async (db, releaseService, repository, unitOfWork) =>
            {
                var builder = new TestDataBuilder(repository, unitOfWork);
                var variableSet = await builder.CreateVariableSetAsync();
                var project = await builder.CreateProjectAsync(variableSet.Id);
                var process = await builder.CreateDeploymentProcessAsync();
                await builder.UpdateProjectProcessIdAsync(project, process.Id);
                var channel = await builder.CreateChannelAsync(project.Id);

                var assembled = DateTimeOffset.Parse("2024-06-01T04:30:00-08:00");
                var result = await releaseService.CreateReleaseAsync(new Squid.Message.Commands.Deployments.Release.CreateReleaseCommand
                {
                    Version = "1.0.0",
                    ProjectId = project.Id,
                    ChannelId = channel.Id,
                    HistoricalCreatedDate = assembled
                });

                var persisted = await db.Set<Release>()
                    .AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == result.Release.Id);

                result.Release.CreatedDate.ShouldBe(assembled.ToUniversalTime());
                persisted.CreatedDate.ShouldBe(assembled.ToUniversalTime());
            });
    }

    [Fact]
    public async Task CreateReleaseCommand_ThroughMediator_PreservesHistoricalCreatedDate()
    {
        await Run<SquidDbContext, IRepository, IUnitOfWork, IMediator>(
            async (db, repository, unitOfWork, mediator) =>
            {
                var builder = new TestDataBuilder(repository, unitOfWork);
                var variableSet = await builder.CreateVariableSetAsync();
                var project = await builder.CreateProjectAsync(variableSet.Id);
                var process = await builder.CreateDeploymentProcessAsync();
                await builder.UpdateProjectProcessIdAsync(project, process.Id);
                var channel = await builder.CreateChannelAsync(project.Id);

                var assembled = DateTimeOffset.Parse("2024-06-01T12:30:00Z");
                var response = await mediator.SendAsync<CreateReleaseCommand, CreateReleaseResponse>(
                    new CreateReleaseCommand
                    {
                        SpaceId = project.SpaceId,
                        Version = "1.0.0",
                        ProjectId = project.Id,
                        ChannelId = channel.Id,
                        HistoricalCreatedDate = assembled
                    });

                var persisted = await db.Set<Release>()
                    .AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == response.Data.Id);

                persisted.CreatedDate.ShouldBe(assembled);
            });
    }

    [Fact]
    public async Task CreateReleaseCommand_InsideImportTransaction_PreservesHistoricalCreatedDate()
    {
        await Run<SquidDbContext, IRepository, IUnitOfWork, IOctopusImportTransactionExecutor>(
            async (db, repository, unitOfWork, transactionExecutor) =>
            {
                var builder = new TestDataBuilder(repository, unitOfWork);
                var variableSet = await builder.CreateVariableSetAsync();
                var project = await builder.CreateProjectAsync(variableSet.Id);
                var process = await builder.CreateDeploymentProcessAsync();
                await builder.UpdateProjectProcessIdAsync(project, process.Id);
                var channel = await builder.CreateChannelAsync(project.Id);
                var mediator = CurrentScope.Resolve<IMediator>();

                var assembled = DateTimeOffset.Parse("2024-06-01T12:30:00Z");
                var releaseId = await transactionExecutor.ExecuteInImportTransactionAsync(
                    new OctopusImportTransactionContext(Guid.NewGuid(), project.SpaceId),
                    async (_, ct) =>
                    {
                        var response = await mediator.SendAsync<CreateReleaseCommand, CreateReleaseResponse>(
                            new CreateReleaseCommand
                            {
                                SpaceId = project.SpaceId,
                                Version = "1.0.0",
                                ProjectId = project.Id,
                                ChannelId = channel.Id,
                                HistoricalCreatedDate = assembled
                            },
                            ct);

                        return response.Data.Id;
                    });

                var persisted = await db.Set<Release>()
                    .AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == releaseId);

                persisted.CreatedDate.ShouldBe(assembled);
            });
    }

    [Fact]
    public async Task ImportAsync_RestoresTestAndPrdCompletionProgression()
    {
        await Run<SquidDbContext, IOctopusImportDeploymentHistoryImporter, ILifecycleProgressionEvaluator>(
            async (db, importer, progressionEvaluator) =>
            {
                var testEnvironment = Environment("Historical TEST", "historical-test");
                var prdEnvironment = Environment("Historical PRD", "historical-prd");
                var lifecycle = new SquidMessageLifecycle
                {
                    Name = "Historical lifecycle",
                    Slug = "historical-lifecycle",
                    SpaceId = SpaceId,
                    ReleaseRetentionKeepForever = true,
                    TentacleRetentionKeepForever = true
                };
                db.Set<Squid.Core.Persistence.Entities.Deployments.Environment>().AddRange(testEnvironment, prdEnvironment);
                db.Set<SquidMessageLifecycle>().Add(lifecycle);
                await db.SaveChangesAsync();

                var testPhase = Phase(lifecycle.Id, testEnvironment.Id, "TEST", 0);
                var prdPhase = Phase(lifecycle.Id, prdEnvironment.Id, "PRD", 1);
                db.Set<LifecyclePhase>().AddRange(testPhase, prdPhase);
                await db.SaveChangesAsync();
                db.Set<LifecyclePhaseEnvironment>().AddRange(
                    new LifecyclePhaseEnvironment
                    {
                        PhaseId = testPhase.Id,
                        EnvironmentId = testEnvironment.Id,
                        TargetType = LifecyclePhaseEnvironmentTargetType.Automatic
                    },
                    new LifecyclePhaseEnvironment
                    {
                        PhaseId = prdPhase.Id,
                        EnvironmentId = prdEnvironment.Id,
                        TargetType = LifecyclePhaseEnvironmentTargetType.Automatic
                    });
                await db.SaveChangesAsync();

                var project = new Project
                {
                    Name = "Historical progression project",
                    Slug = "historical-progression-project",
                    SpaceId = SpaceId,
                    IsDisabled = false,
                    LifecycleId = lifecycle.Id,
                    Json = "{}",
                    IncludedLibraryVariableSetIds = "[]"
                };
                db.Set<Project>().Add(project);
                await db.SaveChangesAsync();

                var channel = new Channel
                {
                    Name = "Default",
                    Slug = "default",
                    ProjectId = project.Id,
                    LifecycleId = lifecycle.Id,
                    SpaceId = SpaceId,
                    IsDefault = true
                };
                db.Set<Channel>().Add(channel);
                await db.SaveChangesAsync();

                var release = new Release
                {
                    Version = "1.0.0",
                    ProjectId = project.Id,
                    ChannelId = channel.Id,
                    SpaceId = SpaceId,
                    CreatedDate = DateTimeOffset.Parse("2024-07-01T00:00:00Z")
                };
                db.Set<Release>().Add(release);
                await db.SaveChangesAsync();

                await importer.ImportAsync(Request(
                    project.Id,
                    channel.Id,
                    release.Id,
                    testEnvironment.Id,
                    "Deploy to TEST",
                    "levi.y",
                    "Users-608",
                    TaskState.Success,
                    DateTimeOffset.Parse("2024-07-10T09:47:02Z"),
                    DateTimeOffset.Parse("2024-07-10T09:48:11Z")));

                var testOnly = await progressionEvaluator.EvaluateProgressionForReleaseAsync(
                    lifecycle.Id,
                    release.Id,
                    CancellationToken.None);
                testOnly.Phases.Single(phase => phase.PhaseId == testPhase.Id).IsComplete.ShouldBeTrue();
                testOnly.Phases.Single(phase => phase.PhaseId == prdPhase.Id).IsComplete.ShouldBeFalse();

                await importer.ImportAsync(Request(
                    project.Id,
                    channel.Id,
                    release.Id,
                    prdEnvironment.Id,
                    "Deploy to PRD",
                    "alice",
                    "Users-1",
                    TaskState.Success,
                    DateTimeOffset.Parse("2024-07-11T09:47:02Z"),
                    DateTimeOffset.Parse("2024-07-11T09:48:11Z")));

                var promoted = await progressionEvaluator.EvaluateProgressionForReleaseAsync(
                    lifecycle.Id,
                    release.Id,
                    CancellationToken.None);
                promoted.Phases.ShouldAllBe(phase => phase.IsComplete);
            });
    }

    [Fact]
    public async Task ArchiveImport_WithHistoricalDeployments_PersistsDeploymentHistory()
    {
        var sessionId = Guid.NewGuid();
        var archivePath = CreateHistoricalDeploymentArchive();

        try
        {
            await Run<SquidDbContext, IOctopusImportPlanningPipeline, IOctopusImportConfirmationOrchestrator>(
                async (db, pipeline, orchestrator) =>
                {
                    db.Set<OctopusImportSession>().Add(new OctopusImportSession
                    {
                        SessionId = sessionId,
                        DestinationSpaceId = SpaceId,
                        OwnerUserId = CurrentUsers.InternalUser.Id,
                        State = OctopusImportSessionState.Validated.ToString(),
                        SourceSummaryJson = "{}",
                        DataVersion = Guid.NewGuid().ToByteArray(),
                        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                        LastStateChangedAt = DateTimeOffset.UtcNow
                    });
                    await db.SaveChangesAsync();
                    db.ChangeTracker.Clear();

                    var snapshot = await pipeline.BuildPreviewAsync(archivePath, SpaceId);
                    snapshot.Graph.Diagnostics.ShouldNotContain(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);
                    snapshot.DependencyPlan.Diagnostics.ShouldNotContain(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);
                    snapshot.PreviewPlan.HasBlockers.ShouldBeFalse();
                    snapshot.PreviewPlan.Resources
                        .Where(resource => resource.SourceType is "Deployment" or "ServerTask")
                        .ShouldAllBe(resource => resource.PreviewAction == OctopusImportPreviewAction.Create);

                    var result = await orchestrator.ConfirmAsync(new OctopusImportConfirmationRequest(
                        sessionId,
                        SpaceId,
                        snapshot.Graph,
                        snapshot.DependencyPlan,
                        snapshot.PreviewPlan));

                    result.State.ShouldBe(
                        OctopusImportSessionState.Succeeded,
                        string.Join(
                            System.Environment.NewLine,
                            result.Result.Diagnostics
                                .Concat(result.Result.Resources.SelectMany(resource => resource.Diagnostics))
                                .Select(diagnostic =>
                                    $"{diagnostic.Severity} {diagnostic.Code} [{diagnostic.SourceId}] {diagnostic.Message}")));
                    result.Result.Succeeded.ShouldBeTrue();

                    var release = await db.Set<Release>()
                        .AsNoTracking()
                        .SingleAsync(candidate => candidate.ProjectId != 0);
                    release.CreatedDate.ShouldBe(DateTimeOffset.Parse("2024-07-01T08:00:00Z"));

                    var deployment = await db.Set<Deployment>()
                        .AsNoTracking()
                        .SingleAsync();
                    var task = await db.Set<Squid.Core.Persistence.Entities.Deployments.ServerTask>()
                        .AsNoTracking()
                        .SingleAsync();
                    var completion = await db.Set<DeploymentCompletion>()
                        .AsNoTracking()
                        .SingleAsync();

                    deployment.CreatedDate.ShouldBe(DateTimeOffset.Parse("2024-07-10T09:47:02Z"));
                    deployment.CreatedBy.ShouldBe(CurrentUsers.InternalUser.Id);
                    deployment.DeployedBy.ShouldBe(CurrentUsers.InternalUser.Id);
                    deployment.Json.ShouldContain("levi.y");
                    deployment.Json.ShouldContain("Users-608");
                    task.State.ShouldBe(TaskState.Success);
                    task.CreatedDate.ShouldBe(DateTimeOffset.Parse("2024-07-10T09:47:02Z"));
                    completion.DeploymentId.ShouldBe(deployment.Id);
                    completion.CompletedTime.ShouldBe(DateTimeOffset.Parse("2024-07-10T09:48:11Z"));

                    result.Result.IdMappings.ShouldContain(mapping =>
                        mapping.SourceType == OctopusResourceKind.Deployment.ToString()
                        && mapping.SourceId == "Deployments-1"
                        && mapping.DestinationId == deployment.Id);
                    result.Result.IdMappings.ShouldContain(mapping =>
                        mapping.SourceType == OctopusResourceKind.ServerTask.ToString()
                        && mapping.SourceId == "ServerTasks-1"
                        && mapping.DestinationId == task.Id);
                });
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task ArchiveImport_WithRealExportArchive_DiagnosesConfirmationFailure()
    {
        var archivePath = System.Environment.GetEnvironmentVariable(RealArchiveEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            return;

        var sessionId = Guid.NewGuid();

        await Run<SquidDbContext, IOctopusImportPlanningPipeline, IOctopusImportConfirmationOrchestrator>(
            async (db, pipeline, orchestrator) =>
            {
                db.Set<OctopusImportSession>().Add(new OctopusImportSession
                {
                    SessionId = sessionId,
                    DestinationSpaceId = SpaceId,
                    OwnerUserId = CurrentUsers.InternalUser.Id,
                    State = OctopusImportSessionState.Validated.ToString(),
                    SourceSummaryJson = "{}",
                    DataVersion = Guid.NewGuid().ToByteArray(),
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                    LastStateChangedAt = DateTimeOffset.UtcNow
                });
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();

                var snapshot = await pipeline.BuildPreviewAsync(archivePath, SpaceId);
                var result = await orchestrator.ConfirmAsync(new OctopusImportConfirmationRequest(
                    sessionId,
                    SpaceId,
                    snapshot.Graph,
                    snapshot.DependencyPlan,
                    snapshot.PreviewPlan));

                result.State.ShouldBe(
                    OctopusImportSessionState.Succeeded,
                    string.Join(
                        System.Environment.NewLine,
                        result.Result.Diagnostics
                            .Concat(result.Result.Resources.SelectMany(resource => resource.Diagnostics))
                            .Select(diagnostic =>
                                $"{diagnostic.Severity} {diagnostic.Code} [{diagnostic.SourceId}] {diagnostic.Message}")));

                var deploymentIds = await db.Set<Deployment>()
                    .AsNoTracking()
                    .Select(deployment => deployment.Id)
                    .ToListAsync();
                var deploymentEvents = await db.Set<Event>()
                    .AsNoTracking()
                    .Where(candidate =>
                        candidate.DeploymentId != null
                        && (candidate.Category == EventCategory.DeploymentSucceeded
                            || candidate.Category == EventCategory.DeploymentFailed
                            || candidate.Category == EventCategory.DeploymentCanceled
                            || candidate.Category == EventCategory.DeploymentTimedOut))
                    .Select(candidate => candidate.DeploymentId!.Value)
                    .ToListAsync();

                deploymentEvents.Count.ShouldBe(deploymentIds.Count);
                deploymentEvents.Distinct().Count().ShouldBe(deploymentIds.Count);
                deploymentIds.ShouldAllBe(deploymentId => deploymentEvents.Contains(deploymentId));
            });
    }

    private static Squid.Core.Persistence.Entities.Deployments.Environment Environment(string name, string slug)
        => new()
        {
            Name = name,
            Slug = slug,
            SpaceId = SpaceId
        };

    private static LifecyclePhase Phase(int lifecycleId, int environmentId, string name, int sortOrder)
        => new()
        {
            LifecycleId = lifecycleId,
            Name = name,
            SortOrder = sortOrder
        };

    private static OctopusImportDeploymentHistoryRequest Request(
        int projectId,
        int channelId,
        int releaseId,
        int environmentId,
        string deploymentName,
        string deployedBy,
        string deployedById,
        string state,
        DateTimeOffset queueTime,
        DateTimeOffset completedTime,
        int spaceId = SpaceId)
        => new(
            spaceId,
            projectId,
            channelId,
            releaseId,
            environmentId,
            deploymentName,
            JsonSerializer.Serialize(new { Id = deploymentName, DeployedBy = deployedBy, DeployedById = deployedById }),
            deployedBy,
            deployedById,
            """["Machines-1"]""",
            queueTime,
            "Deploy",
            $"Deploy {deploymentName}",
            queueTime.AddSeconds(-1),
            queueTime.AddSeconds(1),
            completedTime,
            state,
            state == TaskState.Failed ? "Deployment failed" : string.Empty,
            state == TaskState.Failed,
            (int)(completedTime - queueTime.AddSeconds(1)).TotalSeconds);

    private static string CreateHistoricalDeploymentArchive()
    {
        var documents = new[]
        {
            Document(
                "ProjectGroups-1",
                "ProjectGroup",
                "ProjectGroups-1.json",
                """{"Id":"ProjectGroups-1","Name":"Historical group","Slug":"historical-group"}"""),
            Document(
                "Environments-1",
                "StaticDeploymentEnvironment",
                "Environments-1.json",
                """{"Id":"Environments-1","Name":"Historical TEST","Slug":"historical-test"}"""),
            Document(
                "Lifecycles-1",
                "Lifecycle",
                "Lifecycles-1.json",
                """{"Id":"Lifecycles-1","Name":"Historical lifecycle","Slug":"historical-lifecycle","Phases":[{"Id":"Phases-1","Name":"TEST","AutomaticDeploymentTargets":["Environments-1"]}]}"""),
            Document(
                "Projects-1",
                "Project",
                "Projects-1.json",
                """{"Id":"Projects-1","Name":"Historical project","Slug":"historical-project","ProjectGroupId":"ProjectGroups-1","LifecycleId":"Lifecycles-1","VariableSetId":"variableset-Projects-1","DeploymentProcessId":"deploymentprocess-Projects-1"}"""),
            Document(
                "variableset-Projects-1",
                "ProjectVariables",
                "variableset-Projects-1.json",
                """{"Id":"variableset-Projects-1","OwnerId":"Projects-1","OwnerType":"Project","Variables":[]}"""),
            Document(
                "deploymentprocess-Projects-1",
                "DeploymentProcess",
                "deploymentprocess-Projects-1.json",
                """{"Id":"deploymentprocess-Projects-1","OwnerId":"Projects-1","Steps":[]}"""),
            Document(
                "Channels-1",
                "Channel",
                "Channels-1.json",
                """{"Id":"Channels-1","Name":"Default","Slug":"default","ProjectId":"Projects-1","IsDefault":true}"""),
            Document(
                "Releases-1",
                "Release",
                "Releases-1.json",
                """{"Id":"Releases-1","ProjectId":"Projects-1","ChannelId":"Channels-1","Version":"1.0.0","Assembled":"2024-07-01T08:00:00+00:00"}"""),
            Document(
                "ServerTasks-1",
                "ServerTask",
                "ServerTasks-1.json",
                """{"Id":"ServerTasks-1","Name":"Deploy","Description":"Deploy historical release","ProjectId":"Projects-1","EnvironmentId":"Environments-1","State":"Success","QueueTime":"2024-07-10T09:47:02+00:00","StartTime":"2024-07-10T09:47:03+00:00","CompletedTime":"2024-07-10T09:48:11+00:00","ErrorMessage":"","HasWarningsOrErrors":false,"DurationSeconds":69}"""),
            Document(
                "Deployments-1",
                "Deployment",
                "Deployments-1.json",
                """{"Id":"Deployments-1","Name":"Deploy to TEST","ProjectId":"Projects-1","ChannelId":"Channels-1","EnvironmentId":"Environments-1","ReleaseId":"Releases-1","TaskId":"ServerTasks-1","DeployedBy":"levi.y","DeployedById":"Users-608","DeployedToMachineIds":["Machines-1"],"Created":"2024-07-10T09:47:02+00:00"}""")
        };

        var path = Path.Combine(Path.GetTempPath(), $"squid-octopus-deployment-history-{Guid.NewGuid():N}.zip");
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

    private static (string Id, string DocumentType, string DocumentSource, string Json) Document(
        string id,
        string documentType,
        string documentSource,
        string json)
        => (id, documentType, documentSource, json);

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
}
