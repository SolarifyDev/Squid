using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Moq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization.Exceptions;
using Squid.Core.Services.ProjectTriggers;
using Squid.Core.Services.ProjectTriggers.Runtime;
using Squid.Message.Models.ProjectTriggers;

namespace Squid.IntegrationTests.Services.ProjectTriggers.Runtime;

public partial class ProjectTriggerRuntimeIntegrationTests
{
    [Fact]
    public async Task ConcurrentObservationsReevaluateTheLosingCursorAndDeduplicateAcrossSources()
    {
        var id = await SeedTriggerAsync();
        var together = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialReads = 0;
        _evaluate = async (_, observation, cursor) =>
        {
            if (cursor == null && observation.SourceKey == Source)
            {
                if (Interlocked.Increment(ref initialReads) == 2) together.SetResult();
                await together.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }

            var offset = cursor == null ? 1 : int.Parse(cursor) + 1;
            return new TriggerEvaluation(offset.ToString(), [Occurrence("same-key"), new("filtered", "{}", SkipReason: "No change.")], "Checked.");
        };

        (await Task.WhenAll(ObserveAsync(id), ObserveAsync(id))).Sum().ShouldBe(2);
        (await ObserveAsync(id, "second-source")).ShouldBe(0);

        await Run<SquidDbContext>(async db =>
        {
            var states = await db.Set<ProjectTriggerState>().ToDictionaryAsync(s => s.SourceKey);
            states[Source].Cursor.ShouldBe("2");
            states["second-source"].Cursor.ShouldBe("1");
            states[Source].LastOutcome.ShouldBe("Checked.");
            states[Source].LastCheckedAt.ShouldBe(_clock.GetUtcNow());
            var runs = await db.Set<ProjectTriggerRun>().ToDictionaryAsync(r => r.OccurrenceKey);
            runs.Count.ShouldBe(2);
            runs["filtered"].Status.ShouldBe(ProjectTriggerRunStatuses.Skipped);
            runs["filtered"].Reason.ShouldBe("No change.");
            var run = runs["same-key"];
            run.Status.ShouldBe(ProjectTriggerRunStatuses.Pending);
            (run.Revision, run.ExecutionUserId, run.TriggerType, run.ActionType).ShouldBe((1, Owner, ProjectTriggerTypes.GitRepository, ProjectTriggerActions.CreateRelease));
            JsonDocument.Parse(run.ConfigurationJson).RootElement.GetProperty("schemaVersion").GetInt32().ShouldBe(1);
            JsonSerializer.Deserialize<TriggerActionPlan>(run.ActionPlanJson)!.ChannelId.ShouldBe(3);
        });
    }

    [Fact]
    public async Task FailureWhilePersistingTheCheckRollsBackAllNewOccurrencesAndTheCursor()
    {
        var id = await SeedTriggerAsync();
        await ObserveAsync(id);
        _evaluate = (_, _, _) => Task.FromResult(new TriggerEvaluation("after", [Occurrence("a"), Occurrence("b")], "New."));

        await Run<SquidDbContext>(async db =>
        {
            // Fail at deferred constraint checking, after EF issued both the state and run writes.
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION fail_trigger_test_commit() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'injected cursor commit failure'; END; $$;
                CREATE CONSTRAINT TRIGGER fail_trigger_test_commit AFTER UPDATE ON project_trigger_state
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fail_trigger_test_commit();
                """);
            try
            {
                await Should.ThrowAsync<Exception>(() => ObserveAsync(id));
                (await db.Set<ProjectTriggerState>().SingleAsync()).Cursor.ShouldBe("next");
                (await db.Set<ProjectTriggerRun>().CountAsync()).ShouldBe(1);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_trigger_test_commit ON project_trigger_state; DROP FUNCTION fail_trigger_test_commit();");
            }
        });

        (await ObserveAsync(id)).ShouldBe(2);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task OnlyExecutionEditsDuringExternalEvaluationDiscardStaleInputs(bool changesExecution, int expected)
    {
        var id = await SeedTriggerAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _evaluate = async (_, _, _) =>
        {
            started.SetResult();
            await resume.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return new TriggerEvaluation("stale", [Occurrence("stale")], "Stale.");
        };
        var observation = ObserveAsync(id);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Run<SquidDbContext>(db => db.Set<ProjectTrigger>().Where(t => t.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.Name, "renamed").SetProperty(t => t.Revision, 2).SetProperty(t => t.ExecutionRevision, changesExecution ? 2 : 1)));
        resume.SetResult();

        (await observation).ShouldBe(expected);
        await Run<SquidDbContext>(async db =>
        {
            (await db.Set<ProjectTriggerState>().CountAsync()).ShouldBe(expected);
            (await db.Set<ProjectTriggerRun>().CountAsync()).ShouldBe(expected);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RevocationBeforeReadingTheSourcePreservesTheCursorWithoutCallingTheProvider(bool poll)
    {
        var id = await SeedTriggerAsync();
        _evaluate = (_, _, _) => Task.FromResult(new TriggerEvaluation("old", [], "Baseline.", _clock.GetUtcNow()));
        await ObserveAsync(id);
        var denied = new PermissionDeniedException(Squid.Message.Enums.Permission.ReleaseCreate, "private diagnostic");
        _access.Setup(a => a.EnsureActionAsync(Owner, It.IsAny<ProjectTriggerDefinition>(), It.IsAny<TriggerResourceScope>(), It.IsAny<CancellationToken>())).ThrowsAsync(denied);
        var evaluations = 0;
        var polls = 0;
        _evaluate = (_, _, _) => { evaluations++; return Task.FromResult(new TriggerEvaluation("changed", [Occurrence("forbidden")], "Read.")); };
        await using var scope = OpenScope((_, _) => PollingSource((_, _, at) => { polls++; return new TriggerObservation(Source, "{}", at); }));

        if (poll)
            (await scope.Resolve<IProjectTriggerDueScanner>().ScanDueAsync(1)).ShouldBe(1);
        else
            await Should.ThrowAsync<PermissionDeniedException>(() => ObserveAsync(id));

        (polls, evaluations).ShouldBe((0, 0));
        await Run<SquidDbContext>(async db =>
        {
            var state = await db.Set<ProjectTriggerState>().SingleAsync();
            state.Cursor.ShouldBe("old");
            state.LastOutcome.ShouldBe("Source check authorization was denied.");
            (await db.Set<ProjectTriggerRun>().AnyAsync()).ShouldBeFalse();
            (await db.Set<Release>().AnyAsync()).ShouldBeFalse();
        });
    }

    [Fact]
    public async Task SourceFailureYieldsTheBatchPreservesItsCursorAndHidesRawErrors()
    {
        var broken = await SeedTriggerAsync();
        var healthy = await SeedTriggerAsync();
        _evaluate = (_, _, _) => Task.FromResult(new TriggerEvaluation("old", [], "First poll.", _clock.GetUtcNow()));
        await ObserveAsync(broken);
        var projectId = await InScopeAsync<SquidDbContext, int>(db => db.Set<ProjectTrigger>().Where(t => t.Id == broken).Select(t => t.ProjectId).SingleAsync());
        _evaluate = (_, _, _) => Task.FromResult(new TriggerEvaluation("ok", [], "Healthy.", _clock.GetUtcNow().AddHours(1)));
        await using var scope = OpenScope((_, _) => PollingSource((definition, _, at) => definition.ProjectId == projectId
            ? throw new IOException("secret connection data") : new TriggerObservation(Source, "{}", at)));
        var scanner = scope.Resolve<IProjectTriggerDueScanner>();

        (await scanner.ScanDueAsync(2)).ShouldBe(2);
        (await scanner.ScanDueAsync(2)).ShouldBe(0);
        await Run<SquidDbContext>(async db =>
        {
            var states = await db.Set<ProjectTriggerState>().ToDictionaryAsync(s => s.TriggerId);
            states[broken].Cursor.ShouldBe("old");
            states[broken].LastOutcome.ShouldContain("failed");
            states[broken].LastOutcome.ShouldNotContain("secret");
            states[healthy].Cursor.ShouldBe("ok");
        });
    }

    private static IProjectTriggerPollingSource PollingSource(Func<ProjectTriggerDefinition, string, DateTimeOffset, TriggerObservation> poll)
    {
        var source = new Mock<IProjectTriggerPollingSource>();
        source.SetupGet(x => x.TriggerType).Returns(ProjectTriggerTypes.GitRepository);
        source.SetupGet(x => x.SourceKey).Returns(Source);
        source.Setup(x => x.PollAsync(It.IsAny<ProjectTriggerDefinition>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns((ProjectTriggerDefinition definition, string cursor, DateTimeOffset at, CancellationToken _) => Task.FromResult(poll(definition, cursor, at)));
        return source.Object;
    }

    [Fact]
    public async Task RegisteredPollingSourceBootstrapsAndHonorsNextDueTimeAndBatchLimit()
    {
        await SeedTriggerAsync();
        await SeedTriggerAsync();
        var cursors = new List<string>();
        _evaluate = (_, _, _) => Task.FromResult(new TriggerEvaluation("polled", [], "No change.", _clock.GetUtcNow().AddHours(1)));
        await using var scope = OpenScope((db, actor) => PollingSource((_, cursor, now) =>
        {
            db.Database.CurrentTransaction.ShouldBeNull();
            actor.Id.ShouldBe(Owner);
            actor.IsInternal.ShouldBeFalse();
            cursors.Add(cursor);
            return new TriggerObservation(Source, "{}", now);
        }));
        var scanner = scope.Resolve<IProjectTriggerDueScanner>();

        (await scanner.ScanDueAsync(1)).ShouldBe(1);
        (await scanner.ScanDueAsync(10)).ShouldBe(1);
        (await scanner.ScanDueAsync(10)).ShouldBe(0);
        _clock.Advance(TimeSpan.FromHours(1));
        (await scanner.ScanDueAsync(1)).ShouldBe(1);
        cursors.ShouldBe(new string[] { null, null, "polled" });
        await Run<SquidDbContext>(async db => (await db.Set<ProjectTriggerRun>().AnyAsync()).ShouldBeFalse());
    }
}
