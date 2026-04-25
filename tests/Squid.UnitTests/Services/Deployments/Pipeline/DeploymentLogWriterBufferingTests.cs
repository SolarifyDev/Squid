using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Enums.Deployments;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

public class DeploymentLogWriterBufferingTests
{
    private static DbContextOptions<SquidDbContext> CreateOptions(string dbName = null)
    {
        return new DbContextOptionsBuilder<SquidDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
            .Options;
    }

    // === AddLogAsync buffering ===

    [Fact]
    public async Task AddLogAsync_DoesNotWriteToDb_BeforeFlush()
    {
        var options = CreateOptions();
        await using var writer = new DeploymentLogWriter(options);

        await writer.AddLogAsync(1, 1, ServerTaskLogCategory.Info, "hello", "test");

        await using var db = new SquidDbContext(options);
        var count = await db.Set<ServerTaskLog>().CountAsync();

        count.ShouldBe(0);
    }

    [Fact]
    public async Task AddLogAsync_PopulatesAllFields_AfterFlush()
    {
        var options = CreateOptions();
        var occurredAt = new DateTimeOffset(2026, 3, 19, 12, 0, 0, TimeSpan.Zero);
        await using var writer = new DeploymentLogWriter(options);

        await writer.AddLogAsync(42, 7, ServerTaskLogCategory.Warning, "test msg", "my-source", activityNodeId: 99, occurredAt: occurredAt);
        await writer.FlushAsync();

        await using var db = new SquidDbContext(options);
        var log = await db.Set<ServerTaskLog>().SingleAsync();

        log.ServerTaskId.ShouldBe(42);
        log.SequenceNumber.ShouldBe(7);
        log.Category.ShouldBe(ServerTaskLogCategory.Warning);
        log.MessageText.ShouldBe("test msg");
        log.Source.ShouldBe("my-source");
        log.ActivityNodeId.ShouldBe(99);
        log.OccurredAt.ShouldBe(occurredAt);
    }

    // === AddLogsAsync buffering ===

    [Fact]
    public async Task AddLogsAsync_DoesNotWriteToDb_BeforeFlush()
    {
        var options = CreateOptions();
        await using var writer = new DeploymentLogWriter(options);

        var entries = new List<ServerTaskLogWriteEntry>
        {
            new() { SequenceNumber = 1, Category = ServerTaskLogCategory.Info, MessageText = "batch1", Source = "test" },
            new() { SequenceNumber = 2, Category = ServerTaskLogCategory.Info, MessageText = "batch2", Source = "test" }
        };

        await writer.AddLogsAsync(1, entries);

        await using var db = new SquidDbContext(options);
        var count = await db.Set<ServerTaskLog>().CountAsync();

        count.ShouldBe(0);
    }

    [Fact]
    public async Task AddLogsAsync_PersistsAllEntries_AfterFlush()
    {
        var options = CreateOptions();
        await using var writer = new DeploymentLogWriter(options);

        var entries = new List<ServerTaskLogWriteEntry>
        {
            new() { SequenceNumber = 1, Category = ServerTaskLogCategory.Info, MessageText = "batch1", Source = "src1", Detail = "detail1", ActivityNodeId = 10 },
            new() { SequenceNumber = 2, Category = ServerTaskLogCategory.Error, MessageText = "batch2", Source = "src2", ActivityNodeId = 20 }
        };

        await writer.AddLogsAsync(5, entries);
        await writer.FlushAsync();

        await using var db = new SquidDbContext(options);
        var logs = await db.Set<ServerTaskLog>().OrderBy(l => l.SequenceNumber).ToListAsync();

        logs.Count.ShouldBe(2);
        logs[0].ServerTaskId.ShouldBe(5);
        logs[0].MessageText.ShouldBe("batch1");
        logs[0].Detail.ShouldBe("detail1");
        logs[0].ActivityNodeId.ShouldBe(10);
        logs[1].Category.ShouldBe(ServerTaskLogCategory.Error);
        logs[1].Source.ShouldBe("src2");
        logs[1].ActivityNodeId.ShouldBe(20);
    }

    // === FlushAsync ===

    [Fact]
    public async Task FlushAsync_WritesAllBufferedEntries_InSingleBatch()
    {
        var options = CreateOptions();
        await using var writer = new DeploymentLogWriter(options);

        await writer.AddLogAsync(1, 1, ServerTaskLogCategory.Info, "msg1", "src1");
        await writer.AddLogAsync(1, 2, ServerTaskLogCategory.Warning, "msg2", "src2");
        await writer.AddLogAsync(1, 3, ServerTaskLogCategory.Error, "msg3", "src3");

        await writer.FlushAsync();

        await using var db = new SquidDbContext(options);
        var logs = await db.Set<ServerTaskLog>().OrderBy(l => l.SequenceNumber).ToListAsync();

        logs.Count.ShouldBe(3);
        logs[0].MessageText.ShouldBe("msg1");
        logs[0].Category.ShouldBe(ServerTaskLogCategory.Info);
        logs[1].MessageText.ShouldBe("msg2");
        logs[1].Category.ShouldBe(ServerTaskLogCategory.Warning);
        logs[2].MessageText.ShouldBe("msg3");
        logs[2].Category.ShouldBe(ServerTaskLogCategory.Error);
    }

    [Fact]
    public async Task FlushAsync_EmptyChannel_CompletesWithoutError()
    {
        var options = CreateOptions();
        await using var writer = new DeploymentLogWriter(options);

        await writer.FlushAsync();

        await using var db = new SquidDbContext(options);
        var count = await db.Set<ServerTaskLog>().CountAsync();

        count.ShouldBe(0);
    }

    [Fact]
    public async Task FlushAsync_MixedAddLogAndAddLogs_PersistsAll()
    {
        var options = CreateOptions();
        await using var writer = new DeploymentLogWriter(options);

        await writer.AddLogAsync(1, 1, ServerTaskLogCategory.Info, "single", "src");

        var entries = new List<ServerTaskLogWriteEntry>
        {
            new() { SequenceNumber = 2, Category = ServerTaskLogCategory.Warning, MessageText = "batch1", Source = "src" },
            new() { SequenceNumber = 3, Category = ServerTaskLogCategory.Error, MessageText = "batch2", Source = "src" }
        };
        await writer.AddLogsAsync(1, entries);

        await writer.FlushAsync();

        await using var db = new SquidDbContext(options);
        var count = await db.Set<ServerTaskLog>().CountAsync();

        count.ShouldBe(3);
    }

    // === Background flush ===

    [Fact]
    public async Task BackgroundFlush_DrainsChannel_Periodically()
    {
        var options = CreateOptions();
        await using var writer = new DeploymentLogWriter(options);

        await writer.AddLogAsync(1, 1, ServerTaskLogCategory.Info, "background-test", "test");

        // Poll until flushed or timeout (avoids flaky timing dependency)
        var flushed = await PollUntilAsync(async () =>
        {
            await using var db = new SquidDbContext(options);
            return await db.Set<ServerTaskLog>().CountAsync() > 0;
        }, timeout: TimeSpan.FromSeconds(3));

        flushed.ShouldBeTrue();
    }

    // === Concurrency ===

    [Fact]
    public async Task ConcurrentFlushAsync_DoesNotDuplicateEntries()
    {
        var options = CreateOptions();
        await using var writer = new DeploymentLogWriter(options);

        for (var i = 0; i < 10; i++)
            await writer.AddLogAsync(1, i + 1, ServerTaskLogCategory.Info, $"msg{i}", "test");

        await Task.WhenAll(writer.FlushAsync(), writer.FlushAsync(), writer.FlushAsync());

        await using var db = new SquidDbContext(options);
        var count = await db.Set<ServerTaskLog>().CountAsync();

        count.ShouldBe(10);
    }

    [Fact]
    public async Task ConcurrentAddLog_FromMultipleThreads_AllPersisted()
    {
        var options = CreateOptions();
        await using var writer = new DeploymentLogWriter(options);

        const int threadCount = 8;
        const int logsPerThread = 50;

        var tasks = Enumerable.Range(0, threadCount).Select(t => Task.Run(async () =>
        {
            for (var i = 0; i < logsPerThread; i++)
                await writer.AddLogAsync(1, t * logsPerThread + i + 1, ServerTaskLogCategory.Info, $"thread{t}-msg{i}", "test");
        }));

        await Task.WhenAll(tasks);
        await writer.FlushAsync();

        await using var db = new SquidDbContext(options);
        var count = await db.Set<ServerTaskLog>().CountAsync();

        count.ShouldBe(threadCount * logsPerThread);
    }

    // === Dispose ===

    [Fact]
    public async Task DisposeAsync_FlushesRemainingEntries()
    {
        var options = CreateOptions();
        var writer = new DeploymentLogWriter(options);

        await writer.AddLogAsync(1, 1, ServerTaskLogCategory.Info, "dispose-test", "test");

        await writer.DisposeAsync();

        await using var db = new SquidDbContext(options);
        var logs = await db.Set<ServerTaskLog>().ToListAsync();

        logs.Count.ShouldBe(1);
        logs[0].MessageText.ShouldBe("dispose-test");
    }

    // === Activity nodes (synchronous, not buffered) ===

    [Fact]
    public async Task AddActivityNodeAsync_WritesSynchronously_ReturnsId()
    {
        var options = CreateOptions();
        await using var writer = new DeploymentLogWriter(options);

        var node = await writer.AddActivityNodeAsync(1, null, "Test Node", DeploymentActivityLogNodeType.Task, DeploymentActivityLogNodeStatus.Running, 0);

        node.ShouldNotBeNull();
        node.Id.ShouldBeGreaterThan(0);
        node.Name.ShouldBe("Test Node");

        await using var db = new SquidDbContext(options);
        var persisted = await db.Set<ActivityLog>().FindAsync(node.Id);

        persisted.ShouldNotBeNull();
        persisted.Name.ShouldBe("Test Node");
    }

    // === Phase-6.4: bounded buffer (Agent B P1.1 + Agent D #2) =================
    //
    // Pre-fix: Channel.CreateUnbounded — under DB-stall + high producer rate
    // (100 targets × kubectl-streaming = 30k+ entries/sec), the channel grew
    // until the server pod OOM'd. No high-water mark, no drop policy.
    //
    // Fix: bounded channel with operator-tunable capacity. Default 50_000
    // entries — well above normal deploy log volume but caps memory under
    // sustained DB blips. TryWrite returns false on full; the writer counts
    // drops and emits a structured Serilog warning periodically so operators
    // see the pressure in their logs.
    //
    // Env var SQUID_DEPLOY_LOG_BUFFER_CAPACITY:
    //   - unset / blank      → 50_000 (default)
    //   - integer literal    → that exact capacity
    //   - "unbounded"        → legacy behaviour (dev / test / explicit opt-out)
    //   - unrecognised       → fall back to 50_000

    [Fact]
    public void BufferCapacityEnvVar_ConstantNamePinned()
    {
        DeploymentLogWriter.BufferCapacityEnvVar.ShouldBe("SQUID_DEPLOY_LOG_BUFFER_CAPACITY");
    }

    [Theory]
    [InlineData(null, 50_000)]
    [InlineData("", 50_000)]
    [InlineData("unbounded", null)]
    [InlineData("UNBOUNDED", null)]
    [InlineData("100000", 100_000)]
    [InlineData("0", 50_000)]            // 0 → fall back to default
    [InlineData("-5", 50_000)]            // negative → fall back to default
    [InlineData("garbage", 50_000)]       // unrecognised → fall back
    public void ParseBufferCapacity_Matrix(string envValue, int? expected)
    {
        DeploymentLogWriter.ParseBufferCapacity(envValue).ShouldBe(expected);
    }

    [Fact]
    public async Task AddLogAsync_PastCapacity_DropsExcess_ReportsDropCount()
    {
        // Tiny capacity + many writes → producer hits the bound. With
        // FullMode=Wait + TryWrite, excess writes return false. The writer
        // tracks dropped count and exposes it for operator/monitoring use.
        var options = CreateOptions();
        await using var writer = new DeploymentLogWriter(options, capacity: 4);

        for (var i = 0; i < 100; i++)
            await writer.AddLogAsync(1, i, ServerTaskLogCategory.Info, $"line-{i}", "test");

        // Capacity is 4; we wrote 100 → at least 96 dropped (the background
        // flush may have drained some, freeing slots for later writes — so
        // ≥ 96 - capacity worth of drops is the floor).
        writer.DroppedLogCount.ShouldBeGreaterThan(0,
            customMessage: "writes past the capacity must be reflected in the dropped-log counter; pre-fix unbounded channel would just OOM.");
    }

    [Fact]
    public async Task AddLogAsync_UnboundedMode_NeverDrops()
    {
        // Legacy escape hatch — operator opts out of the bound by passing
        // capacity=null. Same behaviour as the pre-Phase-6 unbounded channel.
        var options = CreateOptions();
        await using var writer = new DeploymentLogWriter(options, capacity: null);

        for (var i = 0; i < 10_000; i++)
            await writer.AddLogAsync(1, i, ServerTaskLogCategory.Info, $"line-{i}", "test");

        writer.DroppedLogCount.ShouldBe(0,
            customMessage: "unbounded mode must never drop, regardless of throughput.");
    }

    [Fact]
    public async Task AddLogAsync_DefaultCapacity_HandlesNormalDeployLoadWithoutDrops()
    {
        // Sanity: a 'normal' load (~5k lines, well under default 50k) must
        // never drop. Pins the default to a meaningful value, not 0/1.
        var options = CreateOptions();
        await using var writer = new DeploymentLogWriter(options);   // default capacity

        for (var i = 0; i < 5_000; i++)
            await writer.AddLogAsync(1, i, ServerTaskLogCategory.Info, $"line-{i}", "test");

        writer.DroppedLogCount.ShouldBe(0);
    }

    // === Helpers ===

    private static async Task<bool> PollUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return true;

            await Task.Delay(50);
        }

        return false;
    }
}
