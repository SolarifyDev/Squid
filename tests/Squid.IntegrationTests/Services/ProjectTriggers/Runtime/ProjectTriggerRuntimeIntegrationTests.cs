using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Squid.Core.Constants;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Identity;
using Squid.Core.Services.Jobs;
using Squid.Core.Services.ProjectTriggers;
using Squid.Core.Services.ProjectTriggers.Management;
using Squid.Core.Services.ProjectTriggers.Runtime;
using Squid.Message.Models.ProjectTriggers;

namespace Squid.IntegrationTests.Services.ProjectTriggers.Runtime;

public partial class ProjectTriggerRuntimeIntegrationTests : TestBase
{
    private const int Owner = 71;
    private const string Config = """{"schemaVersion":1}""";
    private const string Source = "test-source";
    private readonly TestClock _clock = new();
    private readonly Mock<IProjectTriggerAccess> _access = new();
    private readonly Mock<ISquidBackgroundJobClient> _jobs = new(MockBehavior.Strict);
    private readonly List<int> _queuedTasks = new();
    private Func<SquidDbContext, TriggerObservation, string, Task<TriggerEvaluation>> _evaluate;
    private Func<SquidDbContext, ICurrentUser, TriggerActionPlan, Task<TriggerActionResult>> _execute;
    private TriggerResourceScope _allowed = new(3);

    public ProjectTriggerRuntimeIntegrationTests() : base("project_trigger_runtime", "squid_test_project_trigger_runtime")
    {
        _evaluate = (_, observation, _) => Task.FromResult(new TriggerEvaluation("next", [Occurrence("event")], "Matched."));
        _execute = async (db, actor, _) =>
        {
            db.Database.CurrentTransaction.ShouldNotBeNull();
            actor.Id.ShouldBe(Owner);
            actor.Name.ShouldBe($"User {Owner}");
            actor.IsInternal.ShouldBeFalse();
            var release = new Release { ProjectId = 1, ChannelId = 3, SpaceId = 1, Version = "1.0.0" };
            db.Add(release);
            await db.SaveChangesAsync();
            return new TriggerActionResult(release.Id);
        };
    }

    private ILifetimeScope OpenScope(Func<SquidDbContext, ICurrentUser, IProjectTriggerPollingSource> source = null) => CurrentScope.BeginLifetimeScope(builder =>
    {
        builder.RegisterInstance<TimeProvider>(_clock);
        builder.RegisterInstance(_access.Object).As<IProjectTriggerAccess>();
        builder.RegisterInstance(_jobs.Object).As<ISquidBackgroundJobClient>();
        builder.Register(c => Handler(c.Resolve<SquidDbContext>(), c.Resolve<ICurrentUser>())).As<IProjectTriggerHandler>().InstancePerLifetimeScope();
        builder.Register(c => Actions(c.Resolve<SquidDbContext>(), c.Resolve<ICurrentUser>())).As<ITriggerBusinessActions>().InstancePerLifetimeScope();
        if (source != null) builder.Register(c => source(c.Resolve<SquidDbContext>(), c.Resolve<ICurrentUser>())).As<IProjectTriggerPollingSource>().InstancePerLifetimeScope();
    });

    private IProjectTriggerHandler Handler(SquidDbContext db, ICurrentUser actor)
    {
        var handler = new Mock<IProjectTriggerHandler>();
        handler.SetupGet(x => x.TriggerType).Returns(ProjectTriggerTypes.GitRepository);
        handler.SetupGet(x => x.ActionTypes).Returns(new[] { ProjectTriggerActions.CreateRelease });
        handler.Setup(x => x.ValidateAsync(It.IsAny<ProjectTriggerDefinition>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => _allowed);
        handler.Setup(x => x.EvaluateAsync(It.IsAny<ProjectTriggerDefinition>(), It.IsAny<TriggerObservation>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((ProjectTriggerDefinition _, TriggerObservation observation, string cursor, CancellationToken _) =>
            {
                db.Database.CurrentTransaction.ShouldBeNull();
                actor.Id.ShouldBe(Owner);
                actor.IsInternal.ShouldBeFalse();
                return _evaluate(db, observation, cursor);
            });
        return handler.Object;
    }

    private ITriggerBusinessActions Actions(SquidDbContext db, ICurrentUser actor)
    {
        var actions = new Mock<ITriggerBusinessActions>();
        actions.Setup(x => x.ExecuteAsync(It.IsAny<ProjectTriggerDefinition>(), It.IsAny<string>(), It.IsAny<TriggerActionPlan>(), It.IsAny<CancellationToken>()))
            .Returns((ProjectTriggerDefinition _, string _, TriggerActionPlan plan, CancellationToken _) => _execute(db, actor, plan));
        return actions.Object;
    }

    private static TriggerOccurrence Occurrence(string key) => new(key, """{"ref":"main"}""", new TriggerActionPlan { ChannelId = 3 });
    private TriggerObservation Observation(string source = Source) => new(source, "{}", _clock.GetUtcNow());
    private Task<int> ObserveAsync(int id, string source = Source) => InScopeAsync<IProjectTriggerObserver, int>(o => o.ObserveAsync(id, Observation(source)));
    private Task<int> ExecuteAsync(int size = 10) => InScopeAsync<IProjectTriggerWorker, int>(w => w.ExecutePendingAsync(size));
    private Task<int> DispatchAsync(int size = 10) => InScopeAsync<IProjectTriggerWorker, int>(w => w.DispatchPendingAsync(size));
    private Task<ProjectTriggerRun> ReadRunAsync() => InScopeAsync<SquidDbContext, ProjectTriggerRun>(db => db.Set<ProjectTriggerRun>().AsNoTracking().SingleAsync());

    private async Task<TResult> InScopeAsync<T, TResult>(Func<T, Task<TResult>> action)
    {
        await using var scope = OpenScope();
        return await action(scope.Resolve<T>());
    }

    private Task<int> SeedTriggerAsync() => InScopeAsync<SquidDbContext, int>(async db =>
    {
        var project = new Project { Name = "Trigger test", Slug = $"test-{Guid.NewGuid():N}", Json = "{}", IncludedLibraryVariableSetIds = "[]", SpaceId = 1 };
        db.Add(project);
        await db.SaveChangesAsync();
        var trigger = new ProjectTrigger
        {
            Name = "Test source", Description = "", ProjectId = project.Id, SpaceId = 1, Revision = 1, ExecutionUserId = Owner,
            TriggerType = ProjectTriggerTypes.GitRepository, ActionType = ProjectTriggerActions.CreateRelease, ConfigurationJson = Config
        };
        db.Add(trigger);
        await db.SaveChangesAsync();
        return trigger.Id;
    });

    private void QueueWith(Func<int, string> result)
    {
        var executor = new Mock<IDeploymentTaskExecutor>(MockBehavior.Strict);
        executor.Setup(x => x.ProcessAsync(It.IsAny<int>(), It.IsAny<CancellationToken>(), false))
            .Callback<int, CancellationToken, bool>((id, _, _) => _queuedTasks.Add(id)).Returns(Task.CompletedTask);
        _jobs.Setup(j => j.Enqueue(It.IsAny<Expression<Func<IDeploymentTaskExecutor, Task>>>(), HangfireConstants.DefaultQueue))
            .Returns((Expression<Func<IDeploymentTaskExecutor, Task>> call, string _) =>
            {
                call.Compile()(executor.Object).GetAwaiter().GetResult();
                return result(_queuedTasks.Count);
            });
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 4, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan time) => _now += time;
    }
}
