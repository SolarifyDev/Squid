using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Lifecycle.Handlers;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

/// <summary>
/// Live log tail (server side): <see cref="DeploymentActivityLogger"/> persists each incremental
/// script-output chunk to the task log as it streams in (correct category, source, masking, monotonic
/// sequence), and SKIPS the bulk post-completion persist when the result is marked OutputStreamed so
/// those lines are not duplicated. When not streamed, the legacy bulk persist is unchanged.
/// </summary>
public class DeploymentActivityLoggerStreamingTests
{
    private static (DeploymentActivityLogger logger, Mock<IDeploymentLogWriter> writer, List<ServerTaskLogWriteEntry> captured) Build(IEnumerable<VariableDto> variables = null)
    {
        var writer = new Mock<IDeploymentLogWriter>();
        var captured = new List<ServerTaskLogWriteEntry>();

        writer.Setup(w => w.AddLogsAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<ServerTaskLogWriteEntry>>(), It.IsAny<CancellationToken>()))
            .Callback<int, IReadOnlyCollection<ServerTaskLogWriteEntry>, CancellationToken>((_, entries, _) => captured.AddRange(entries))
            .Returns(Task.CompletedTask);
        writer.Setup(w => w.AddActivityNodeAsync(It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<string>(), It.IsAny<DeploymentActivityLogNodeType>(), It.IsAny<DeploymentActivityLogNodeStatus>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityLog { Id = 1 });
        writer.Setup(w => w.AddLogAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<ServerTaskLogCategory>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var logger = new DeploymentActivityLogger(writer.Object);
        logger.Initialize(new DeploymentTaskContext { ServerTaskId = 1, Variables = variables?.ToList() });
        return (logger, writer, captured);
    }

    // Masker is initialized on DeploymentStarting (as in production, before any action streams).
    private static Task StartAsync(DeploymentActivityLogger logger)
        => logger.HandleAsync(new DeploymentStartingEvent(new DeploymentEventContext()), CancellationToken.None);

    [Fact]
    public async Task ScriptProgress_PersistsChunkLive_WithSourceCategoryMaskingAndSequence()
    {
        var (logger, _, captured) = Build(new[] { new VariableDto { Value = "topsecret", IsSensitive = true } });
        await StartAsync(logger);

        var chunk = new List<ScriptOutputLine>
        {
            new("deploying app", IsStdErr: false),
            new("warning: topsecret leaked", IsStdErr: true)
        };

        await logger.HandleAsync(new ScriptProgressReceivedEvent(new DeploymentEventContext
        {
            StepDisplayOrder = 1, MachineName = "agent-x", ActionSortOrder = 0, ScriptOutputChunk = chunk
        }), CancellationToken.None);

        captured.Count.ShouldBe(2);

        captured[0].MessageText.ShouldBe("deploying app");
        captured[0].Category.ShouldBe(ServerTaskLogCategory.Info);
        captured[0].Source.ShouldBe("agent-x");

        captured[1].Category.ShouldBe(ServerTaskLogCategory.Error);          // stderr → Error
        captured[1].MessageText.ShouldNotContain("topsecret");               // sensitive value masked
        captured[1].MessageText.ShouldContain("********");
        captured[1].SequenceNumber.ShouldBeGreaterThan(captured[0].SequenceNumber);   // monotonic
    }

    [Fact]
    public async Task ScriptProgress_EmptyChunk_DoesNotPersist()
    {
        var (logger, writer, _) = Build();

        await logger.HandleAsync(new ScriptProgressReceivedEvent(new DeploymentEventContext
        {
            StepDisplayOrder = 1, MachineName = "agent-x", ScriptOutputChunk = new List<ScriptOutputLine>()
        }), CancellationToken.None);

        writer.Verify(w => w.AddLogsAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<ServerTaskLogWriteEntry>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScriptOutputReceived_OutputStreamed_SkipsBulkPersist_NoDuplication()
    {
        var (logger, writer, _) = Build();

        await logger.HandleAsync(new ScriptOutputReceivedEvent(new DeploymentEventContext
        {
            StepDisplayOrder = 1, MachineName = "agent-x", ActionSortOrder = 0,
            ScriptResult = new ScriptExecutionResult { LogLines = new List<string> { "a", "b" }, OutputStreamed = true }
        }), CancellationToken.None);

        writer.Verify(w => w.AddLogsAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<ServerTaskLogWriteEntry>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScriptOutputReceived_NotStreamed_PersistsBulk_LegacyBehavior()
    {
        var (logger, _, captured) = Build();

        await logger.HandleAsync(new ScriptOutputReceivedEvent(new DeploymentEventContext
        {
            StepDisplayOrder = 1, MachineName = "agent-x", ActionSortOrder = 0,
            ScriptResult = new ScriptExecutionResult { LogLines = new List<string> { "a", "b" }, OutputStreamed = false }
        }), CancellationToken.None);

        captured.Select(e => e.MessageText).ShouldBe(new[] { "a", "b" });
    }
}
