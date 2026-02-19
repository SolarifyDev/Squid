using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.IntegrationTests.Helpers;

namespace Squid.IntegrationTests.Deployments.ServerTasks;

public class IntegrationServerTaskLog : ServerTaskFixtureBase
{
    private async Task<int> CreateServerTaskAsync()
    {
        var taskId = 0;
        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var task = new ServerTask
            {
                Name = "Log Test Task",
                Description = "Task for log integration test",
                QueueTime = DateTimeOffset.UtcNow,
                State = "Running",
                ServerTaskType = "Deploy",
                ProjectId = 1,
                EnvironmentId = 1,
                SpaceId = 1,
                LastModified = DateTimeOffset.UtcNow,
                BusinessProcessState = "Executing",
                StateOrder = 2,
                Weight = 1,
                BatchId = 0,
                JSON = string.Empty,
                HasWarningsOrErrors = false,
                ServerNodeId = Guid.NewGuid(),
                DurationSeconds = 0,
                DataVersion = Array.Empty<byte>()
            };

            await repository.InsertAsync(task, CancellationToken.None).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            taskId = task.Id;
        }).ConfigureAwait(false);

        return taskId;
    }

    [Fact]
    public async Task AddLog_SingleEntry_Persisted()
    {
        await Run<IServerTaskLogDataProvider>(async provider =>
        {
            var taskId = await CreateServerTaskAsync().ConfigureAwait(false);

            await provider.AddLogAsync(new ServerTaskLog
            {
                ServerTaskId = taskId,
                Category = "Info",
                MessageText = "Deployment started",
                Source = "Test Machine",
                OccurredAt = DateTimeOffset.UtcNow,
                SequenceNumber = 1
            }).ConfigureAwait(false);

            var logs = await provider.GetLogsByTaskIdAsync(taskId).ConfigureAwait(false);

            logs.ShouldNotBeNull();
            logs.Count.ShouldBe(1);
            logs[0].MessageText.ShouldBe("Deployment started");
            logs[0].Category.ShouldBe("Info");
            logs[0].Source.ShouldBe("Test Machine");
            logs[0].SequenceNumber.ShouldBe(1);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task AddLogs_BatchInsert_AllPersisted()
    {
        await Run<IServerTaskLogDataProvider>(async provider =>
        {
            var taskId = await CreateServerTaskAsync().ConfigureAwait(false);

            var logs = Enumerable.Range(1, 100).Select(i => new ServerTaskLog
            {
                ServerTaskId = taskId,
                Category = i % 10 == 0 ? "Warning" : "Info",
                MessageText = $"Log line {i}",
                Source = "Test Machine",
                OccurredAt = DateTimeOffset.UtcNow.AddMilliseconds(i),
                SequenceNumber = i
            }).ToList();

            await provider.AddLogsAsync(logs).ConfigureAwait(false);

            var result = await provider.GetLogsByTaskIdAsync(taskId).ConfigureAwait(false);

            result.Count.ShouldBe(100);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task GetLogsByTaskId_OrderedBySequenceNumber()
    {
        await Run<IServerTaskLogDataProvider>(async provider =>
        {
            var taskId = await CreateServerTaskAsync().ConfigureAwait(false);

            // Insert out of order
            await provider.AddLogAsync(new ServerTaskLog
            {
                ServerTaskId = taskId,
                Category = "Info",
                MessageText = "Third",
                Source = "Machine",
                OccurredAt = DateTimeOffset.UtcNow,
                SequenceNumber = 3
            }).ConfigureAwait(false);

            await provider.AddLogAsync(new ServerTaskLog
            {
                ServerTaskId = taskId,
                Category = "Info",
                MessageText = "First",
                Source = "Machine",
                OccurredAt = DateTimeOffset.UtcNow,
                SequenceNumber = 1
            }).ConfigureAwait(false);

            await provider.AddLogAsync(new ServerTaskLog
            {
                ServerTaskId = taskId,
                Category = "Info",
                MessageText = "Second",
                Source = "Machine",
                OccurredAt = DateTimeOffset.UtcNow,
                SequenceNumber = 2
            }).ConfigureAwait(false);

            var logs = await provider.GetLogsByTaskIdAsync(taskId).ConfigureAwait(false);

            logs[0].MessageText.ShouldBe("First");
            logs[1].MessageText.ShouldBe("Second");
            logs[2].MessageText.ShouldBe("Third");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task GetLogsByCategory_FiltersCorrectly()
    {
        await Run<IServerTaskLogDataProvider>(async provider =>
        {
            var taskId = await CreateServerTaskAsync().ConfigureAwait(false);

            var logs = new List<ServerTaskLog>
            {
                new() { ServerTaskId = taskId, Category = "Info", MessageText = "Info 1", Source = "M", OccurredAt = DateTimeOffset.UtcNow, SequenceNumber = 1 },
                new() { ServerTaskId = taskId, Category = "Warning", MessageText = "Warning 1", Source = "M", OccurredAt = DateTimeOffset.UtcNow, SequenceNumber = 2 },
                new() { ServerTaskId = taskId, Category = "Error", MessageText = "Error 1", Source = "M", OccurredAt = DateTimeOffset.UtcNow, SequenceNumber = 3 },
                new() { ServerTaskId = taskId, Category = "Info", MessageText = "Info 2", Source = "M", OccurredAt = DateTimeOffset.UtcNow, SequenceNumber = 4 },
                new() { ServerTaskId = taskId, Category = "Error", MessageText = "Error 2", Source = "M", OccurredAt = DateTimeOffset.UtcNow, SequenceNumber = 5 }
            };

            await provider.AddLogsAsync(logs).ConfigureAwait(false);

            var errors = await provider.GetLogsByTaskIdAndCategoryAsync(taskId, "Error").ConfigureAwait(false);
            errors.Count.ShouldBe(2);
            errors[0].MessageText.ShouldBe("Error 1");
            errors[1].MessageText.ShouldBe("Error 2");

            var warnings = await provider.GetLogsByTaskIdAndCategoryAsync(taskId, "Warning").ConfigureAwait(false);
            warnings.Count.ShouldBe(1);

            var infos = await provider.GetLogsByTaskIdAndCategoryAsync(taskId, "Info").ConfigureAwait(false);
            infos.Count.ShouldBe(2);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task GetLogCount_ReturnsCorrectCount()
    {
        await Run<IServerTaskLogDataProvider>(async provider =>
        {
            var taskId = await CreateServerTaskAsync().ConfigureAwait(false);

            var logs = Enumerable.Range(1, 25).Select(i => new ServerTaskLog
            {
                ServerTaskId = taskId,
                Category = "Info",
                MessageText = $"Log {i}",
                Source = "Machine",
                OccurredAt = DateTimeOffset.UtcNow,
                SequenceNumber = i
            }).ToList();

            await provider.AddLogsAsync(logs).ConfigureAwait(false);

            var count = await provider.GetLogCountByTaskIdAsync(taskId).ConfigureAwait(false);
            count.ShouldBe(25);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task GetLogsByTaskId_IsolatedBetweenTasks()
    {
        await Run<IServerTaskLogDataProvider>(async provider =>
        {
            var taskId1 = await CreateServerTaskAsync().ConfigureAwait(false);
            var taskId2 = await CreateServerTaskAsync().ConfigureAwait(false);

            await provider.AddLogAsync(new ServerTaskLog
            {
                ServerTaskId = taskId1,
                Category = "Info",
                MessageText = "Task 1 log",
                Source = "Machine",
                OccurredAt = DateTimeOffset.UtcNow,
                SequenceNumber = 1
            }).ConfigureAwait(false);

            await provider.AddLogsAsync(new List<ServerTaskLog>
            {
                new() { ServerTaskId = taskId2, Category = "Info", MessageText = "Task 2 log A", Source = "Machine", OccurredAt = DateTimeOffset.UtcNow, SequenceNumber = 1 },
                new() { ServerTaskId = taskId2, Category = "Info", MessageText = "Task 2 log B", Source = "Machine", OccurredAt = DateTimeOffset.UtcNow, SequenceNumber = 2 }
            }).ConfigureAwait(false);

            var task1Logs = await provider.GetLogsByTaskIdAsync(taskId1).ConfigureAwait(false);
            task1Logs.Count.ShouldBe(1);
            task1Logs[0].MessageText.ShouldBe("Task 1 log");

            var task2Logs = await provider.GetLogsByTaskIdAsync(taskId2).ConfigureAwait(false);
            task2Logs.Count.ShouldBe(2);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task AddLogs_EmptyList_NoException()
    {
        await Run<IServerTaskLogDataProvider>(async provider =>
        {
            await provider.AddLogsAsync(new List<ServerTaskLog>()).ConfigureAwait(false);
            await provider.AddLogsAsync(null).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task AddLog_LargeMessageText_Persisted()
    {
        await Run<IServerTaskLogDataProvider>(async provider =>
        {
            var taskId = await CreateServerTaskAsync().ConfigureAwait(false);
            var largeText = new string('X', 50000);

            await provider.AddLogAsync(new ServerTaskLog
            {
                ServerTaskId = taskId,
                Category = "Info",
                MessageText = largeText,
                Source = "Machine",
                OccurredAt = DateTimeOffset.UtcNow,
                SequenceNumber = 1
            }).ConfigureAwait(false);

            var logs = await provider.GetLogsByTaskIdAsync(taskId).ConfigureAwait(false);
            logs.Count.ShouldBe(1);
            logs[0].MessageText.Length.ShouldBe(50000);
        }).ConfigureAwait(false);
    }
}
