using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Lifecycle.Handlers;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Enums.Deployments;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

/// <summary>
/// Integration tests: real DeploymentLogWriter (InMemory DB) + real DeploymentActivityLogger +
/// real lifecycle publisher. Verifies eventual consistency — all buffered logs are persisted
/// to DB after flush on deployment completion.
///
/// Note: ActivityLog status update assertions are excluded because InMemory provider
/// does not reliably support ExecuteUpdateAsync. Status updates are covered by mock-based tests.
/// </summary>
public class DeploymentLogWriterIntegrationTests : IAsyncDisposable
{
    private readonly DbContextOptions<SquidDbContext> _dbOptions;
    private readonly DeploymentLogWriter _logWriter;
    private readonly IDeploymentLifecycle _lifecycle;

    public DeploymentLogWriterIntegrationTests()
    {
        _dbOptions = new DbContextOptionsBuilder<SquidDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _logWriter = new DeploymentLogWriter(_dbOptions);

        var logger = new DeploymentActivityLogger(_logWriter);
        _lifecycle = new DeploymentLifecyclePublisher(new IDeploymentLifecycleHandler[] { logger });
    }

    public async ValueTask DisposeAsync() => await _logWriter.DisposeAsync();

    // === Successful deployment: full lifecycle → all logs flushed ===

    [Fact]
    public async Task SuccessfulDeployment_AllLogsPersistedAfterCompletion()
    {
        var ctx = CreateContext(taskId: 1);
        _lifecycle.Initialize(ctx);

        await EmitAsync(new DeploymentStartingEvent(new DeploymentEventContext()));

        await EmitAsync(new StepStartingEvent(new DeploymentEventContext { StepName = "Deploy Web", StepDisplayOrder = 1 }));
        await EmitAsync(new StepExecutingOnTargetEvent(new DeploymentEventContext { StepName = "Deploy Web", StepDisplayOrder = 1, MachineName = "k8s-node-1" }));

        await EmitAsync(new ActionExecutingEvent(new DeploymentEventContext { StepDisplayOrder = 1, MachineName = "k8s-node-1", ActionSortOrder = 1 }));
        await EmitAsync(new ActionRunningEvent(new DeploymentEventContext { StepDisplayOrder = 1, MachineName = "k8s-node-1", ActionName = "Run Script", ActionSortOrder = 1 }));

        var scriptResult = new ScriptExecutionResult
        {
            Success = true,
            ExitCode = 0,
            LogLines = new List<string> { "Deploying...", "Done!" }
        };
        await EmitAsync(new ScriptOutputReceivedEvent(new DeploymentEventContext { StepDisplayOrder = 1, MachineName = "k8s-node-1", ActionSortOrder = 1, ScriptResult = scriptResult }));

        await EmitAsync(new ActionSucceededEvent(new DeploymentEventContext { StepDisplayOrder = 1, MachineName = "k8s-node-1", ActionName = "Run Script", ActionSortOrder = 1, ExitCode = 0 }));
        await EmitAsync(new StepCompletedEvent(new DeploymentEventContext { StepName = "Deploy Web", StepDisplayOrder = 1 }));

        // Succeeded — triggers FlushAsync internally
        await EmitAsync(new DeploymentSucceededEvent(new DeploymentEventContext()));

        // Verify activity tree (node creation is synchronous — always persisted immediately)
        await using var db = new SquidDbContext(_dbOptions);
        var nodes = await db.Set<ActivityLog>().OrderBy(n => n.Id).ToListAsync();
        nodes.Count.ShouldBeGreaterThanOrEqualTo(3);

        var taskNode = nodes.First(n => n.NodeType == DeploymentActivityLogNodeType.Task);
        taskNode.Name.ShouldContain("Deploy");

        var stepNode = nodes.First(n => n.NodeType == DeploymentActivityLogNodeType.Step);
        stepNode.ParentId.ShouldBe(taskNode.Id);
        stepNode.Name.ShouldContain("Deploy Web");

        var actionNode = nodes.First(n => n.NodeType == DeploymentActivityLogNodeType.Action);
        actionNode.ParentId.ShouldBe(stepNode.Id);

        // Verify logs: all buffered entries flushed after DeploymentSucceededEvent
        var logs = await db.Set<ServerTaskLog>().OrderBy(l => l.SequenceNumber).ToListAsync();
        logs.Count.ShouldBeGreaterThanOrEqualTo(5);

        logs.First().Category.ShouldBe(ServerTaskLogCategory.Info);
        logs.First().MessageText.ShouldContain("Deploying");

        // Script output lines persisted
        var scriptLogs = logs.Where(l => l.MessageText.Contains("Deploying...") || l.MessageText.Contains("Done!")).ToList();
        scriptLogs.Count.ShouldBe(2);

        // Final success log present
        logs.ShouldContain(l => l.MessageText.Contains("completed successfully"));

        // All logs have correct taskId
        logs.ShouldAllBe(l => l.ServerTaskId == 1);

        // Sequence numbers unique and increasing
        var sequences = logs.Select(l => l.SequenceNumber).ToList();
        sequences.ShouldBe(sequences.Distinct().OrderBy(s => s).ToList());
    }

    // === Failed deployment: error logs flushed ===

    [Fact]
    public async Task FailedDeployment_ErrorLogPersistedAfterFlush()
    {
        var ctx = CreateContext(taskId: 2);
        _lifecycle.Initialize(ctx);

        await EmitAsync(new DeploymentStartingEvent(new DeploymentEventContext()));
        await EmitAsync(new StepStartingEvent(new DeploymentEventContext { StepName = "Deploy DB", StepDisplayOrder = 1 }));
        await EmitAsync(new ActionExecutingEvent(new DeploymentEventContext { StepDisplayOrder = 1, MachineName = "db-node", ActionSortOrder = 1 }));
        await EmitAsync(new ActionFailedEvent(new DeploymentEventContext { StepDisplayOrder = 1, MachineName = "db-node", ActionName = "Migrate", ActionSortOrder = 1, Error = "Connection refused" }));
        await EmitAsync(new StepCompletedEvent(new DeploymentEventContext { StepName = "Deploy DB", StepDisplayOrder = 1, Failed = true }));

        // Failed — triggers FlushAsync internally
        await EmitAsync(new DeploymentFailedEvent(new DeploymentEventContext { Error = "Deployment failed: Connection refused" }));

        await using var db = new SquidDbContext(_dbOptions);

        // Error logs persisted
        var logs = await db.Set<ServerTaskLog>().Where(l => l.ServerTaskId == 2).ToListAsync();
        var errorLogs = logs.Where(l => l.Category == ServerTaskLogCategory.Error).ToList();
        errorLogs.Count.ShouldBeGreaterThanOrEqualTo(2); // action error + deployment error
        errorLogs.ShouldContain(l => l.MessageText.Contains("Connection refused"));

        // Starting log also persisted (buffered earlier, flushed on failure)
        logs.ShouldContain(l => l.MessageText.Contains("Deploying"));

        // All logs under correct taskId
        logs.ShouldAllBe(l => l.ServerTaskId == 2);
    }

    // === Cancelled deployment: warning log flushed ===

    [Fact]
    public async Task CancelledDeployment_WarningLogPersistedAfterFlush()
    {
        var ctx = CreateContext(taskId: 3);
        _lifecycle.Initialize(ctx);

        await EmitAsync(new DeploymentStartingEvent(new DeploymentEventContext()));
        await EmitAsync(new StepStartingEvent(new DeploymentEventContext { StepName = "Long step", StepDisplayOrder = 1 }));

        // Cancelled — triggers FlushAsync internally
        await EmitAsync(new DeploymentCancelledEvent(new DeploymentEventContext()));

        await using var db = new SquidDbContext(_dbOptions);

        var logs = await db.Set<ServerTaskLog>().Where(l => l.ServerTaskId == 3).ToListAsync();

        // Warning log persisted
        logs.ShouldContain(l => l.Category == ServerTaskLogCategory.Warning && l.MessageText.Contains("cancelled"));

        // Starting log also persisted
        logs.ShouldContain(l => l.MessageText.Contains("Deploying"));
    }

    // === Activity node → log association ===

    [Fact]
    public async Task LogEntries_AssociatedWithCorrectActivityNodes()
    {
        var ctx = CreateContext(taskId: 4);
        _lifecycle.Initialize(ctx);

        await EmitAsync(new DeploymentStartingEvent(new DeploymentEventContext()));
        await EmitAsync(new StepStartingEvent(new DeploymentEventContext { StepName = "Step A", StepDisplayOrder = 1 }));
        await EmitAsync(new StepExecutingOnTargetEvent(new DeploymentEventContext { StepName = "Step A", StepDisplayOrder = 1, MachineName = "node-1" }));
        await EmitAsync(new StepCompletedEvent(new DeploymentEventContext { StepName = "Step A", StepDisplayOrder = 1 }));
        await EmitAsync(new DeploymentSucceededEvent(new DeploymentEventContext()));

        await using var db = new SquidDbContext(_dbOptions);
        var nodes = await db.Set<ActivityLog>().Where(n => n.ServerTaskId == 4).ToListAsync();
        var logs = await db.Set<ServerTaskLog>().Where(l => l.ServerTaskId == 4).OrderBy(l => l.SequenceNumber).ToListAsync();

        var taskNode = nodes.First(n => n.NodeType == DeploymentActivityLogNodeType.Task);
        var stepNode = nodes.First(n => n.NodeType == DeploymentActivityLogNodeType.Step);

        // Starting log → task node
        var startingLog = logs.First(l => l.MessageText.Contains("Deploying"));
        startingLog.ActivityNodeId.ShouldBe(taskNode.Id);

        // Step executing log → step node
        var executingLog = logs.First(l => l.MessageText.Contains("Executing step"));
        executingLog.ActivityNodeId.ShouldBe(stepNode.Id);

        // Step completed log → step node
        var completedStepLog = logs.First(l => l.MessageText.Contains("completed successfully") && l.ActivityNodeId == stepNode.Id);
        completedStepLog.ShouldNotBeNull();
    }

    // === Multi-step: sequence ordering ===

    [Fact]
    public async Task MultiStep_LogSequenceMonotonicallyIncreasing()
    {
        var ctx = CreateContext(taskId: 5);
        _lifecycle.Initialize(ctx);

        await EmitAsync(new DeploymentStartingEvent(new DeploymentEventContext()));

        for (var i = 1; i <= 3; i++)
        {
            await EmitAsync(new StepStartingEvent(new DeploymentEventContext { StepName = $"Step {i}", StepDisplayOrder = i }));
            await EmitAsync(new StepExecutingOnTargetEvent(new DeploymentEventContext { StepName = $"Step {i}", StepDisplayOrder = i, MachineName = "node" }));
            await EmitAsync(new StepCompletedEvent(new DeploymentEventContext { StepName = $"Step {i}", StepDisplayOrder = i }));
        }

        await EmitAsync(new DeploymentSucceededEvent(new DeploymentEventContext()));

        await using var db = new SquidDbContext(_dbOptions);
        var logs = await db.Set<ServerTaskLog>().Where(l => l.ServerTaskId == 5).OrderBy(l => l.SequenceNumber).ToListAsync();

        logs.Count.ShouldBeGreaterThanOrEqualTo(7);

        // Strictly increasing sequence numbers
        for (var i = 1; i < logs.Count; i++)
            logs[i].SequenceNumber.ShouldBeGreaterThan(logs[i - 1].SequenceNumber);

        // Activity tree: 1 task + 3 steps
        var nodes = await db.Set<ActivityLog>().Where(n => n.ServerTaskId == 5).ToListAsync();
        nodes.Count(n => n.NodeType == DeploymentActivityLogNodeType.Task).ShouldBe(1);
        nodes.Count(n => n.NodeType == DeploymentActivityLogNodeType.Step).ShouldBe(3);
    }

    // === Script output with stderr → correct category mapping ===

    [Fact]
    public async Task ScriptOutput_StderrLines_PersistedAsErrorCategory()
    {
        var ctx = CreateContext(taskId: 6);
        _lifecycle.Initialize(ctx);

        await EmitAsync(new DeploymentStartingEvent(new DeploymentEventContext()));
        await EmitAsync(new StepStartingEvent(new DeploymentEventContext { StepName = "Script", StepDisplayOrder = 1 }));
        await EmitAsync(new ActionExecutingEvent(new DeploymentEventContext { StepDisplayOrder = 1, MachineName = "node", ActionSortOrder = 1 }));

        var scriptResult = new ScriptExecutionResult
        {
            Success = false,
            ExitCode = 1,
            LogLines = new List<string> { "stdout line", "error line" },
            StderrLines = new List<string> { "error line" }
        };
        await EmitAsync(new ScriptOutputReceivedEvent(new DeploymentEventContext { StepDisplayOrder = 1, MachineName = "node", ActionSortOrder = 1, ScriptResult = scriptResult }));

        await EmitAsync(new ActionFailedEvent(new DeploymentEventContext { StepDisplayOrder = 1, MachineName = "node", ActionName = "Script", ActionSortOrder = 1, Error = "exit code 1" }));
        await EmitAsync(new StepCompletedEvent(new DeploymentEventContext { StepName = "Script", StepDisplayOrder = 1, Failed = true }));
        await EmitAsync(new DeploymentFailedEvent(new DeploymentEventContext { Error = "Script failed" }));

        await using var db = new SquidDbContext(_dbOptions);
        var logs = await db.Set<ServerTaskLog>().Where(l => l.ServerTaskId == 6).ToListAsync();

        // stdout line → Info category
        logs.ShouldContain(l => l.Category == ServerTaskLogCategory.Info && l.MessageText.Contains("stdout line"));

        // stderr line → Error category
        logs.ShouldContain(l => l.Category == ServerTaskLogCategory.Error && l.MessageText.Contains("error line"));
    }

    // === Helpers ===

    private Task EmitAsync(DeploymentLifecycleEvent @event)
        => _lifecycle.EmitAsync(@event, CancellationToken.None);

    private static DeploymentTaskContext CreateContext(int taskId)
    {
        return new DeploymentTaskContext
        {
            ServerTaskId = taskId,
            Project = new Project { Name = "WebApp" },
            Release = new Squid.Core.Persistence.Entities.Deployments.Release { Version = "1.0.0" },
            Environment = new Squid.Core.Persistence.Entities.Deployments.Environment { Name = "Production" },
            Deployment = new Deployment { ProjectId = 1, EnvironmentId = 1 },
            AllTargets = new List<Machine>()
        };
    }
}
