using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ActivityLog;
using ActivityLogEntity = Squid.Core.Persistence.Entities.Deployments.ActivityLog;

namespace Squid.IntegrationTests.Deployments.Logs;

[Collection("Sequential")]
public class IntegrationActivityLog : IntegrationTestBase,
    IClassFixture<IntegrationFixture<IntegrationActivityLog>>
{
    public IntegrationActivityLog(IntegrationFixture<IntegrationActivityLog> fixture) : base(fixture) { }

    private async Task<int> CreateServerTaskAsync()
    {
        var taskId = 0;
        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var task = new ServerTask
            {
                Name = "Activity Log Test Task",
                Description = "Task for activity log integration test",
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
    public async Task AddNode_RootTaskNode_Persisted()
    {
        await Run<IActivityLogDataProvider>(async provider =>
        {
            var taskId = await CreateServerTaskAsync().ConfigureAwait(false);

            var node = await provider.AddNodeAsync(new ActivityLogEntity
            {
                ServerTaskId = taskId,
                ParentId = null,
                Name = "Deploy to Production",
                NodeType = "Task",
                Category = "Info",
                Status = "Running",
                StartedAt = DateTimeOffset.UtcNow,
                SortOrder = 0
            }).ConfigureAwait(false);

            node.Id.ShouldBeGreaterThan(0);

            var tree = await provider.GetTreeByTaskIdAsync(taskId).ConfigureAwait(false);
            tree.Count.ShouldBe(1);
            tree[0].Name.ShouldBe("Deploy to Production");
            tree[0].NodeType.ShouldBe("Task");
            tree[0].ParentId.ShouldBeNull();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task BuildTree_TaskStepActionHierarchy_Persisted()
    {
        await Run<IActivityLogDataProvider>(async provider =>
        {
            var taskId = await CreateServerTaskAsync().ConfigureAwait(false);

            // Root: Task node
            var taskNode = await provider.AddNodeAsync(new ActivityLogEntity
            {
                ServerTaskId = taskId,
                ParentId = null,
                Name = "Deploy v1.0.0",
                NodeType = "Task",
                Status = "Running",
                StartedAt = DateTimeOffset.UtcNow,
                SortOrder = 0
            }).ConfigureAwait(false);

            // Child: Step 1
            var step1 = await provider.AddNodeAsync(new ActivityLogEntity
            {
                ServerTaskId = taskId,
                ParentId = taskNode.Id,
                Name = "Run Database Migrations",
                NodeType = "Step",
                Status = "Running",
                StartedAt = DateTimeOffset.UtcNow,
                SortOrder = 1
            }).ConfigureAwait(false);

            // Child: Step 2
            var step2 = await provider.AddNodeAsync(new ActivityLogEntity
            {
                ServerTaskId = taskId,
                ParentId = taskNode.Id,
                Name = "Deploy Web Application",
                NodeType = "Step",
                Status = "Pending",
                StartedAt = DateTimeOffset.UtcNow,
                SortOrder = 2
            }).ConfigureAwait(false);

            // Grandchild: Action under Step 1
            var action1 = await provider.AddNodeAsync(new ActivityLogEntity
            {
                ServerTaskId = taskId,
                ParentId = step1.Id,
                Name = "Run migration script",
                NodeType = "Action",
                Status = "Running",
                StartedAt = DateTimeOffset.UtcNow,
                SortOrder = 1
            }).ConfigureAwait(false);

            // Log entry under Action 1
            await provider.AddNodeAsync(new ActivityLogEntity
            {
                ServerTaskId = taskId,
                ParentId = action1.Id,
                Name = "Script output",
                NodeType = "LogEntry",
                Category = "Info",
                LogText = "Applying migration 001_create_users...",
                StartedAt = DateTimeOffset.UtcNow,
                SortOrder = 1
            }).ConfigureAwait(false);

            // Verify full tree
            var tree = await provider.GetTreeByTaskIdAsync(taskId).ConfigureAwait(false);
            tree.Count.ShouldBe(5);

            // Verify children of task node
            var taskChildren = await provider.GetChildrenAsync(taskNode.Id).ConfigureAwait(false);
            taskChildren.Count.ShouldBe(2);
            taskChildren[0].Name.ShouldBe("Run Database Migrations");
            taskChildren[1].Name.ShouldBe("Deploy Web Application");

            // Verify children of step 1
            var step1Children = await provider.GetChildrenAsync(step1.Id).ConfigureAwait(false);
            step1Children.Count.ShouldBe(1);
            step1Children[0].NodeType.ShouldBe("Action");

            // Verify children of action 1
            var action1Children = await provider.GetChildrenAsync(action1.Id).ConfigureAwait(false);
            action1Children.Count.ShouldBe(1);
            action1Children[0].NodeType.ShouldBe("LogEntry");
            action1Children[0].LogText.ShouldBe("Applying migration 001_create_users...");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task UpdateNodeStatus_RunningToSuccess_Updated()
    {
        await Run<IActivityLogDataProvider>(async provider =>
        {
            var taskId = await CreateServerTaskAsync().ConfigureAwait(false);
            var startTime = DateTimeOffset.UtcNow;

            var node = await provider.AddNodeAsync(new ActivityLogEntity
            {
                ServerTaskId = taskId,
                ParentId = null,
                Name = "Step 1",
                NodeType = "Step",
                Status = "Running",
                StartedAt = startTime,
                SortOrder = 0
            }).ConfigureAwait(false);

            node.Status.ShouldBe("Running");
            node.EndedAt.ShouldBeNull();

            var endTime = DateTimeOffset.UtcNow;
            await provider.UpdateNodeStatusAsync(node.Id, "Success", endTime).ConfigureAwait(false);

            var updated = await provider.GetNodeByIdAsync(node.Id).ConfigureAwait(false);
            updated.Status.ShouldBe("Success");
            updated.EndedAt.ShouldNotBeNull();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task UpdateNodeStatus_RunningToFailed_Updated()
    {
        await Run<IActivityLogDataProvider>(async provider =>
        {
            var taskId = await CreateServerTaskAsync().ConfigureAwait(false);

            var node = await provider.AddNodeAsync(new ActivityLogEntity
            {
                ServerTaskId = taskId,
                ParentId = null,
                Name = "Failing Step",
                NodeType = "Step",
                Status = "Running",
                StartedAt = DateTimeOffset.UtcNow,
                SortOrder = 0
            }).ConfigureAwait(false);

            await provider.UpdateNodeStatusAsync(node.Id, "Failed", DateTimeOffset.UtcNow).ConfigureAwait(false);

            var updated = await provider.GetNodeByIdAsync(node.Id).ConfigureAwait(false);
            updated.Status.ShouldBe("Failed");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task GetTreeByTaskId_OrderedBySortOrder()
    {
        await Run<IActivityLogDataProvider>(async provider =>
        {
            var taskId = await CreateServerTaskAsync().ConfigureAwait(false);

            // Insert in reverse order
            await provider.AddNodeAsync(new ActivityLogEntity
            {
                ServerTaskId = taskId,
                ParentId = null,
                Name = "Third",
                NodeType = "Step",
                Status = "Pending",
                StartedAt = DateTimeOffset.UtcNow,
                SortOrder = 3
            }).ConfigureAwait(false);

            await provider.AddNodeAsync(new ActivityLogEntity
            {
                ServerTaskId = taskId,
                ParentId = null,
                Name = "First",
                NodeType = "Task",
                Status = "Running",
                StartedAt = DateTimeOffset.UtcNow,
                SortOrder = 1
            }).ConfigureAwait(false);

            await provider.AddNodeAsync(new ActivityLogEntity
            {
                ServerTaskId = taskId,
                ParentId = null,
                Name = "Second",
                NodeType = "Step",
                Status = "Running",
                StartedAt = DateTimeOffset.UtcNow,
                SortOrder = 2
            }).ConfigureAwait(false);

            var tree = await provider.GetTreeByTaskIdAsync(taskId).ConfigureAwait(false);

            tree[0].Name.ShouldBe("First");
            tree[1].Name.ShouldBe("Second");
            tree[2].Name.ShouldBe("Third");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task GetTreeByTaskId_IsolatedBetweenTasks()
    {
        await Run<IActivityLogDataProvider>(async provider =>
        {
            var taskId1 = await CreateServerTaskAsync().ConfigureAwait(false);
            var taskId2 = await CreateServerTaskAsync().ConfigureAwait(false);

            await provider.AddNodeAsync(new ActivityLogEntity
            {
                ServerTaskId = taskId1,
                Name = "Task 1 Root",
                NodeType = "Task",
                Status = "Running",
                StartedAt = DateTimeOffset.UtcNow,
                SortOrder = 0
            }).ConfigureAwait(false);

            await provider.AddNodeAsync(new ActivityLogEntity
            {
                ServerTaskId = taskId2,
                Name = "Task 2 Root",
                NodeType = "Task",
                Status = "Running",
                StartedAt = DateTimeOffset.UtcNow,
                SortOrder = 0
            }).ConfigureAwait(false);

            await provider.AddNodeAsync(new ActivityLogEntity
            {
                ServerTaskId = taskId2,
                Name = "Task 2 Step",
                NodeType = "Step",
                Status = "Pending",
                StartedAt = DateTimeOffset.UtcNow,
                SortOrder = 1
            }).ConfigureAwait(false);

            var tree1 = await provider.GetTreeByTaskIdAsync(taskId1).ConfigureAwait(false);
            tree1.Count.ShouldBe(1);

            var tree2 = await provider.GetTreeByTaskIdAsync(taskId2).ConfigureAwait(false);
            tree2.Count.ShouldBe(2);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task UpdateNodeStatus_NonExistentNode_NoException()
    {
        await Run<IActivityLogDataProvider>(async provider =>
        {
            await provider.UpdateNodeStatusAsync(999999, "Success", DateTimeOffset.UtcNow).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task AddNode_WithLogText_Persisted()
    {
        await Run<IActivityLogDataProvider>(async provider =>
        {
            var taskId = await CreateServerTaskAsync().ConfigureAwait(false);

            var node = await provider.AddNodeAsync(new ActivityLogEntity
            {
                ServerTaskId = taskId,
                Name = "Script Output",
                NodeType = "LogEntry",
                Category = "Error",
                LogText = "ERROR: Connection refused to database server at 10.0.0.5:5432",
                StartedAt = DateTimeOffset.UtcNow,
                SortOrder = 0
            }).ConfigureAwait(false);

            var retrieved = await provider.GetNodeByIdAsync(node.Id).ConfigureAwait(false);
            retrieved.LogText.ShouldBe("ERROR: Connection refused to database server at 10.0.0.5:5432");
            retrieved.Category.ShouldBe("Error");
            retrieved.NodeType.ShouldBe("LogEntry");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task GetChildren_NoChildren_ReturnsEmpty()
    {
        await Run<IActivityLogDataProvider>(async provider =>
        {
            var taskId = await CreateServerTaskAsync().ConfigureAwait(false);

            var node = await provider.AddNodeAsync(new ActivityLogEntity
            {
                ServerTaskId = taskId,
                Name = "Leaf Node",
                NodeType = "Action",
                Status = "Success",
                StartedAt = DateTimeOffset.UtcNow,
                SortOrder = 0
            }).ConfigureAwait(false);

            var children = await provider.GetChildrenAsync(node.Id).ConfigureAwait(false);
            children.ShouldNotBeNull();
            children.Count.ShouldBe(0);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task FullDeploymentTreeLifecycle_StatusTransitions()
    {
        await Run<IActivityLogDataProvider>(async provider =>
        {
            var taskId = await CreateServerTaskAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;

            // Create task root
            var taskNode = await provider.AddNodeAsync(new ActivityLogEntity
            {
                ServerTaskId = taskId,
                Name = "Deploy v2.0.0 to Production",
                NodeType = "Task",
                Status = "Running",
                StartedAt = now,
                SortOrder = 0
            }).ConfigureAwait(false);

            // Step 1: Running
            var step1 = await provider.AddNodeAsync(new ActivityLogEntity
            {
                ServerTaskId = taskId,
                ParentId = taskNode.Id,
                Name = "Database Migration",
                NodeType = "Step",
                Status = "Running",
                StartedAt = now,
                SortOrder = 1
            }).ConfigureAwait(false);

            // Step 1 → Action 1
            var action1 = await provider.AddNodeAsync(new ActivityLogEntity
            {
                ServerTaskId = taskId,
                ParentId = step1.Id,
                Name = "Run migration script",
                NodeType = "Action",
                Status = "Running",
                StartedAt = now,
                SortOrder = 1
            }).ConfigureAwait(false);

            // Mark action as success
            await provider.UpdateNodeStatusAsync(action1.Id, "Success", now.AddSeconds(5)).ConfigureAwait(false);

            // Mark step as success
            await provider.UpdateNodeStatusAsync(step1.Id, "Success", now.AddSeconds(6)).ConfigureAwait(false);

            // Step 2: Fails
            var step2 = await provider.AddNodeAsync(new ActivityLogEntity
            {
                ServerTaskId = taskId,
                ParentId = taskNode.Id,
                Name = "Deploy Application",
                NodeType = "Step",
                Status = "Running",
                StartedAt = now.AddSeconds(7),
                SortOrder = 2
            }).ConfigureAwait(false);

            await provider.UpdateNodeStatusAsync(step2.Id, "Failed", now.AddSeconds(12)).ConfigureAwait(false);

            // Mark task as failed
            await provider.UpdateNodeStatusAsync(taskNode.Id, "Failed", now.AddSeconds(12)).ConfigureAwait(false);

            // Verify final state
            var tree = await provider.GetTreeByTaskIdAsync(taskId).ConfigureAwait(false);
            tree.Count.ShouldBe(4);

            var rootNode = tree.First(n => n.NodeType == "Task");
            rootNode.Status.ShouldBe("Failed");
            rootNode.EndedAt.ShouldNotBeNull();

            var successStep = tree.First(n => n.Name == "Database Migration");
            successStep.Status.ShouldBe("Success");

            var failedStep = tree.First(n => n.Name == "Deploy Application");
            failedStep.Status.ShouldBe("Failed");

            var successAction = tree.First(n => n.NodeType == "Action");
            successAction.Status.ShouldBe("Success");
        }).ConfigureAwait(false);
    }
}
