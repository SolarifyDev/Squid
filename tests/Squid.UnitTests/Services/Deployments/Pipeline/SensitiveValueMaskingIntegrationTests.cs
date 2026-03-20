using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Lifecycle.Handlers;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;
using Squid.Message.Constants;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

public class SensitiveValueMaskingIntegrationTests
{
    private record CapturedLog(ServerTaskLogCategory Category, string Message, string Source, long? ActivityNodeId);

    [Fact]
    public async Task SensitiveVariableValue_MaskedInTaskLog()
    {
        var (lifecycle, ctx, logs) = CreateHarness();
        ctx.Variables.Add(new VariableDto { Name = "DbPassword", Value = "super-secret-pw", IsSensitive = true });

        lifecycle.Initialize(ctx);
        await EmitAsync(lifecycle, new DeploymentStartingEvent(new DeploymentEventContext()));

        var startLog = logs.First(l => l.Message.Contains("Deploying"));
        startLog.Message.ShouldNotContain("super-secret-pw");
    }

    [Fact]
    public async Task SensitiveVariableValue_MaskedInScriptOutput()
    {
        var (lifecycle, ctx, logs) = CreateHarness();
        ctx.Variables.Add(new VariableDto { Name = "ApiToken", Value = "tok_abc123xyz", IsSensitive = true });

        lifecycle.Initialize(ctx);
        await EmitAsync(lifecycle, new DeploymentStartingEvent(new DeploymentEventContext()));
        await EmitAsync(lifecycle, new StepStartingEvent(new DeploymentEventContext { StepName = "Deploy", StepDisplayOrder = 1 }));
        await EmitAsync(lifecycle, new ActionExecutingEvent(new DeploymentEventContext { StepDisplayOrder = 1, MachineName = "node-1", ActionSortOrder = 1 }));

        var scriptResult = new ScriptExecutionResult
        {
            Success = true,
            ExitCode = 0,
            LogLines = new List<string> { "Connecting with tok_abc123xyz", "Done" }
        };
        await EmitAsync(lifecycle, new ScriptOutputReceivedEvent(new DeploymentEventContext { StepDisplayOrder = 1, MachineName = "node-1", ActionSortOrder = 1, ScriptResult = scriptResult }));

        var sensitiveLog = logs.FirstOrDefault(l => l.Message.Contains("tok_abc123xyz"));
        sensitiveLog.ShouldBeNull("Sensitive token should have been masked");

        var maskedLog = logs.FirstOrDefault(l => l.Message.Contains(SensitiveValueMasker.MaskToken) && l.Message.Contains("Connecting"));
        maskedLog.ShouldNotBeNull("Masked log line should exist");
    }

    [Fact]
    public async Task EndpointSensitiveVariable_MaskedInLogs()
    {
        var (lifecycle, ctx, logs) = CreateHarness();
        ctx.AllTargetsContext.Add(new DeploymentTargetContext
        {
            Machine = new Machine { Name = "k8s-node" },
            EndpointContext = new EndpointContext { EndpointJson = "{}" },
            EndpointVariables = new List<VariableDto>
            {
                new() { Name = "Squid.Account.Token", Value = "endpoint-secret-token", IsSensitive = true }
            }
        });

        lifecycle.Initialize(ctx);
        await EmitAsync(lifecycle, new DeploymentStartingEvent(new DeploymentEventContext()));

        await EmitAsync(lifecycle, new StepStartingEvent(new DeploymentEventContext { StepName = "Deploy", StepDisplayOrder = 1 }));
        await EmitAsync(lifecycle, new ActionExecutingEvent(new DeploymentEventContext { StepDisplayOrder = 1, MachineName = "k8s-node", ActionSortOrder = 1 }));

        var scriptResult = new ScriptExecutionResult
        {
            Success = false,
            ExitCode = 1,
            LogLines = new List<string> { "Error: auth failed with endpoint-secret-token" }
        };
        await EmitAsync(lifecycle, new ScriptOutputReceivedEvent(new DeploymentEventContext { StepDisplayOrder = 1, MachineName = "k8s-node", ActionSortOrder = 1, ScriptResult = scriptResult }));

        logs.ShouldNotContain(l => l.Message.Contains("endpoint-secret-token"));
        logs.ShouldContain(l => l.Message.Contains(SensitiveValueMasker.MaskToken));
    }

    [Fact]
    public async Task NonSensitiveVariableValue_NotMasked()
    {
        var (lifecycle, ctx, logs) = CreateHarness();
        ctx.Variables.Add(new VariableDto { Name = "AppName", Value = "my-web-app", IsSensitive = false });

        lifecycle.Initialize(ctx);
        await EmitAsync(lifecycle, new DeploymentStartingEvent(new DeploymentEventContext()));
        await EmitAsync(lifecycle, new StepStartingEvent(new DeploymentEventContext { StepName = "Deploy", StepDisplayOrder = 1 }));
        await EmitAsync(lifecycle, new ActionExecutingEvent(new DeploymentEventContext { StepDisplayOrder = 1, MachineName = "node", ActionSortOrder = 1 }));

        var scriptResult = new ScriptExecutionResult
        {
            Success = true,
            ExitCode = 0,
            LogLines = new List<string> { "Deploying my-web-app" }
        };
        await EmitAsync(lifecycle, new ScriptOutputReceivedEvent(new DeploymentEventContext { StepDisplayOrder = 1, MachineName = "node", ActionSortOrder = 1, ScriptResult = scriptResult }));

        logs.ShouldContain(l => l.Message == "Deploying my-web-app");
    }

    [Fact]
    public async Task MultipleSensitiveValues_AllMasked()
    {
        var (lifecycle, ctx, logs) = CreateHarness();
        ctx.Variables.Add(new VariableDto { Name = "Password", Value = "pass123", IsSensitive = true });
        ctx.Variables.Add(new VariableDto { Name = "Secret", Value = "key-abc", IsSensitive = true });

        lifecycle.Initialize(ctx);
        await EmitAsync(lifecycle, new DeploymentStartingEvent(new DeploymentEventContext()));
        await EmitAsync(lifecycle, new StepStartingEvent(new DeploymentEventContext { StepName = "Deploy", StepDisplayOrder = 1 }));
        await EmitAsync(lifecycle, new ActionExecutingEvent(new DeploymentEventContext { StepDisplayOrder = 1, MachineName = "node", ActionSortOrder = 1 }));

        var scriptResult = new ScriptExecutionResult
        {
            Success = true,
            ExitCode = 0,
            LogLines = new List<string> { "auth=pass123 apikey=key-abc done" }
        };
        await EmitAsync(lifecycle, new ScriptOutputReceivedEvent(new DeploymentEventContext { StepDisplayOrder = 1, MachineName = "node", ActionSortOrder = 1, ScriptResult = scriptResult }));

        logs.ShouldNotContain(l => l.Message.Contains("pass123"));
        logs.ShouldNotContain(l => l.Message.Contains("key-abc"));
    }

    [Fact]
    public async Task ErrorMessage_SensitiveValueMasked()
    {
        var (lifecycle, ctx, logs) = CreateHarness();
        ctx.Variables.Add(new VariableDto { Name = "Token", Value = "bearer-xyz", IsSensitive = true });

        lifecycle.Initialize(ctx);
        await EmitAsync(lifecycle, new DeploymentStartingEvent(new DeploymentEventContext()));

        await EmitAsync(lifecycle, new DeploymentFailedEvent(new DeploymentEventContext { Error = "Connection failed: bearer-xyz rejected" }));

        var errorLog = logs.FirstOrDefault(l => l.Category == ServerTaskLogCategory.Error);
        errorLog.ShouldNotBeNull();
        errorLog.Message.ShouldNotContain("bearer-xyz");
        errorLog.Message.ShouldContain(SensitiveValueMasker.MaskToken);
    }

    // === Helpers ===

    private static Task EmitAsync(IDeploymentLifecycle lifecycle, DeploymentLifecycleEvent @event)
        => lifecycle.EmitAsync(@event, CancellationToken.None);

    private static (IDeploymentLifecycle Lifecycle, DeploymentTaskContext Ctx, ConcurrentBag<CapturedLog> Logs) CreateHarness()
    {
        var logs = new ConcurrentBag<CapturedLog>();
        var mock = CreateLogWriterMock(logs);
        var logger = new DeploymentActivityLogger(mock.Object);
        var lifecycle = new DeploymentLifecyclePublisher(new IDeploymentLifecycleHandler[] { logger });

        var ctx = new DeploymentTaskContext
        {
            ServerTaskId = 1,
            Project = new Project { Name = "WebApp" },
            Release = new Squid.Core.Persistence.Entities.Deployments.Release { Version = "1.0.0" },
            Environment = new Squid.Core.Persistence.Entities.Deployments.Environment { Name = "Production" },
            Deployment = new Deployment { ProjectId = 1, EnvironmentId = 1 },
            Variables = new List<VariableDto>(),
            AllTargets = new List<Machine>(),
            AllTargetsContext = new List<DeploymentTargetContext>()
        };

        return (lifecycle, ctx, logs);
    }

    private static Mock<IDeploymentLogWriter> CreateLogWriterMock(ConcurrentBag<CapturedLog> logs)
    {
        var nextNodeId = 0L;
        var mock = new Mock<IDeploymentLogWriter>();

        mock.Setup(x => x.AddActivityNodeAsync(It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<string>(), It.IsAny<DeploymentActivityLogNodeType>(), It.IsAny<DeploymentActivityLogNodeStatus>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int taskId, long? parentId, string name, DeploymentActivityLogNodeType nodeType, DeploymentActivityLogNodeStatus status, int sortOrder, CancellationToken _) =>
            {
                return new ActivityLog
                {
                    Id = Interlocked.Increment(ref nextNodeId),
                    ServerTaskId = taskId,
                    ParentId = parentId,
                    Name = name,
                    NodeType = nodeType,
                    Status = status,
                    SortOrder = sortOrder,
                    StartedAt = DateTimeOffset.UtcNow
                };
            });

        mock.Setup(x => x.UpdateActivityNodeStatusAsync(It.IsAny<long>(), It.IsAny<DeploymentActivityLogNodeStatus>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mock.Setup(x => x.AddLogAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<ServerTaskLogCategory>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Callback((int taskId, long seq, ServerTaskLogCategory cat, string msg, string src, long? nodeId, DateTimeOffset? at, CancellationToken _) =>
            {
                logs.Add(new CapturedLog(cat, msg, src, nodeId));
            })
            .Returns(Task.CompletedTask);

        mock.Setup(x => x.AddLogsAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<ServerTaskLogWriteEntry>>(), It.IsAny<CancellationToken>()))
            .Callback((int taskId, IReadOnlyCollection<ServerTaskLogWriteEntry> entries, CancellationToken _) =>
            {
                foreach (var entry in entries)
                    logs.Add(new CapturedLog(entry.Category, entry.MessageText, entry.Source, entry.ActivityNodeId));
            })
            .Returns(Task.CompletedTask);

        mock.Setup(x => x.FlushAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mock.Setup(x => x.GetTreeByTaskIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<ActivityLog>());

        return mock;
    }
}
