using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Moq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Jobs;
using Squid.Message.Models.Deployments.Deployment;

namespace Squid.IntegrationTests.Deployments.Pipeline;

public class IntegrationTriggerDeploymentCreation : TriggerDeploymentFixtureBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualCreate_QueuesRegularConsumerAndPersistsJobId(bool scheduled)
    {
        var command = await SeedReleaseAsync();
        var jobs = new Mock<ISquidBackgroundJobClient>(MockBehavior.Strict);
        var time = DateTimeOffset.UtcNow.AddHours(1);
        if (scheduled)
        {
            command.QueueTime = time;
            jobs.Setup(j => j.Schedule(It.Is<Expression<Func<IDeploymentTaskExecutor, Task>>>(e => IsManualConsumer(e)),
                time, It.IsAny<string>())).Returns("manual-job");
        }
        else
        {
            jobs.Setup(j => j.Enqueue(It.Is<Expression<Func<IDeploymentTaskExecutor, Task>>>(e => IsManualConsumer(e)),
                It.IsAny<string>())).Returns("manual-job");
        }

        await Run<IDeploymentService, SquidDbContext>(async (service, db) =>
        {
            var created = await service.CreateDeploymentAsync(command);
            var task = await db.Set<ServerTask>().AsNoTracking().SingleAsync(t => t.Id == created.TaskId);

            task.State.ShouldBe(TaskState.Pending);
            task.JobId.ShouldBe("manual-job");
            jobs.VerifyAll();
        }, b => b.RegisterInstance(jobs.Object));
    }

    [Fact]
    public async Task TriggerCreate_WritesLinkedTaskAndDeploymentInCallerTransactionWithoutQueueing()
    {
        var command = await SeedReleaseAsync();
        command.Comments = "from observation";
        command.UseGuidedFailure = true;
        var jobs = new Mock<ISquidBackgroundJobClient>(MockBehavior.Strict);

        await Run<IDeploymentService, SquidDbContext>(async (service, db) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var created = await service.CreateDeploymentForTriggerAsync(command);
            var task = await db.Set<ServerTask>().AsNoTracking().SingleAsync(t => t.Id == created.TaskId);
            var deployment = await db.Set<Deployment>().AsNoTracking().SingleAsync(d => d.Id == created.Deployment.Id);
            var payload = JsonSerializer.Deserialize<DeploymentRequestPayload>(deployment.Json);

            task.State.ShouldBe(TaskState.Pending);
            task.JobId.ShouldBeNullOrEmpty();
            deployment.TaskId.ShouldBe(task.Id);
            deployment.ReleaseId.ShouldBe(command.ReleaseId);
            deployment.ProcessSnapshotId.GetValueOrDefault().ShouldBeGreaterThan(0);
            payload.Comments.ShouldBe(command.Comments);
            payload.UseGuidedFailure.ShouldBeTrue();

            await transaction.RollbackAsync();
        }, b => b.RegisterInstance(jobs.Object));

        await Run<SquidDbContext>(async db =>
        {
            (await db.Set<ServerTask>().CountAsync()).ShouldBe(0);
            (await db.Set<Deployment>().CountAsync()).ShouldBe(0);
        });
        jobs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TriggerCreate_WithoutCallerTransactionFailsBeforeWriting()
    {
        var command = await SeedReleaseAsync();

        await Run<IDeploymentService, SquidDbContext>(async (service, db) =>
        {
            var error = await Should.ThrowAsync<InvalidOperationException>(() => service.CreateDeploymentForTriggerAsync(command));

            error.Message.ShouldContain("transaction", Case.Insensitive);
            (await db.Set<ServerTask>().CountAsync()).ShouldBe(0);
            (await db.Set<Deployment>().CountAsync()).ShouldBe(0);
        });
    }

    [Fact]
    public async Task TriggerCreate_RejectsTheSameMachineSelectionAsManualCreate()
    {
        var command = await SeedReleaseAsync();
        command.SpecificMachineIds = ["7"];
        command.ExcludedMachineIds = ["7"];

        await Run<IDeploymentService, SquidDbContext>(async (service, db) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var manual = await Should.ThrowAsync<DeploymentValidationException>(() => service.CreateDeploymentAsync(command));
            var trigger = await Should.ThrowAsync<DeploymentValidationException>(() => service.CreateDeploymentForTriggerAsync(command));

            trigger.Message.ShouldBe(manual.Message);
            trigger.Message.ShouldContain("cannot overlap");
            (await db.Set<ServerTask>().CountAsync()).ShouldBe(0);
        });
    }

    private static bool IsManualConsumer(Expression<Func<IDeploymentTaskExecutor, Task>> job) =>
        ((MethodCallExpression)job.Body).Method.Name == nameof(IDeploymentTaskExecutor.ProcessAsync);
}
