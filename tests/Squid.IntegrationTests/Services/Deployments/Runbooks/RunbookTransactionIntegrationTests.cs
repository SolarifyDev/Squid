using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Runbooks;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.Deployments.Runbook;
using Squid.Message.Models.Deployments.Runbook;

namespace Squid.IntegrationTests.Services.Deployments.Runbooks;

public class RunbookTransactionIntegrationTests : TestBase
{
    private static readonly FailAtStageObserver Observer = new();

    public RunbookTransactionIntegrationTests()
        : base(
            "_runbook_transaction_",
            "squid_it_runbook_transaction_20261105",
            builder => builder.RegisterInstance(Observer)
                .As<IRunbookAggregateWriteObserver>()
                .SingleInstance())
    {
    }

    [Fact]
    public async Task Create_SuccessUsesOneBusinessSave()
    {
        var project = await Run<SquidDbContext, ProjectSnapshot>(async db =>
        {
            return await SeedProjectAsync(db);
        });

        Observer.BeforeBusinessSaveCount = 0;

        await Run<IRunbookService>(async service =>
        {
            var created = await service.CreateRunbookAsync(new CreateRunbookCommand
            {
                Runbook = new CreateRunbookModel
                {
                    ProjectId = project.Id,
                    SpaceId = project.SpaceId,
                    Name = "Single save"
                }
            });

            created.Id.ShouldBeGreaterThan(0);
            created.ProcessId.ShouldBeGreaterThan(0);
            created.VariableSetId.ShouldBeGreaterThan(0);
        });

        Observer.BeforeBusinessSaveCount.ShouldBe(1);
    }

    [Fact]
    public async Task Create_FailureAtAnyStage_RollsBackAllRowsAndAudit()
    {
        var project = await Run<SquidDbContext, ProjectSnapshot>(async db =>
        {
            var seeded = await SeedProjectAsync(db);
            await db.SaveChangesAsync();
            return seeded;
        });

        foreach (var stage in Enum.GetValues<RunbookAggregateCreateStage>())
        {
            Observer.FailAt = stage;
            var name = $"Failure at {stage}";

            await Run<IRunbookService>(async service =>
            {
                await Should.ThrowAsync<InvalidOperationException>(
                    () => service.CreateRunbookAsync(new CreateRunbookCommand
                    {
                        Runbook = new CreateRunbookModel
                        {
                            ProjectId = project.Id,
                            SpaceId = project.SpaceId,
                            Name = name
                        }
                    }));
            });

            await Run<SquidDbContext>(async db =>
            {
                db.ChangeTracker.Clear();
                (await db.Set<Runbook>().AnyAsync(r => r.ProjectId == project.Id)).ShouldBeFalse();
                (await db.Set<DeploymentProcess>().CountAsync(p => p.ProjectId == project.Id)).ShouldBe(1);
                (await db.Set<VariableSet>().CountAsync(v => v.SpaceId == project.SpaceId)).ShouldBe(1);
                (await db.Set<Squid.Core.Persistence.Entities.Events.Event>()
                        .ToListAsync())
                    .Any(e => e.ReferencesJson.Contains(name))
                    .ShouldBeFalse();
            });
        }

        Observer.FailAt = null;
    }

    [Fact]
    public async Task Copy_FailureAfterActionScopes_RollsBackCopyAndPreservesSource()
    {
        var project = await Run<SquidDbContext, ProjectSnapshot>(async db =>
        {
            return await SeedProjectAsync(db);
        });

        var sourceRunbookId = 0;
        await Run<IRunbookService>(async service =>
        {
            var source = await service.CreateRunbookAsync(new CreateRunbookCommand
            {
                Runbook = new CreateRunbookModel
                {
                    ProjectId = project.Id,
                    SpaceId = project.SpaceId,
                    Name = "Copy source"
                }
            });
            sourceRunbookId = source.Id;
        });

        var sourceProcessId = await Run<SquidDbContext, int>(async db =>
        {
            var runbook = await db.Set<Runbook>().SingleAsync(r => r.Id == sourceRunbookId);
            var step = new DeploymentStep
            {
                ProcessId = runbook.ProcessId,
                StepOrder = 1,
                Name = "Source step",
                StepType = "Action"
            };
            db.Set<DeploymentStep>().Add(step);
            await db.SaveChangesAsync();

            var action = new DeploymentAction
            {
                StepId = step.Id,
                ActionOrder = 1,
                Name = "Source action",
                ActionType = "Octopus.Script"
            };
            db.Set<DeploymentAction>().Add(action);
            await db.SaveChangesAsync();

            db.Set<ActionEnvironment>().Add(new ActionEnvironment
            {
                ActionId = action.Id,
                EnvironmentId = 1
            });
            await db.SaveChangesAsync();
            return runbook.ProcessId;
        });

        Observer.FailAtCopyStage = RunbookAggregateCopyStage.ActionScopesInserted;

        await Run<IRunbookService>(async service =>
        {
            await Should.ThrowAsync<InvalidOperationException>(
                () => service.CopyRunbookAsync(new CopyRunbookCommand
                {
                    SourceRunbookId = sourceRunbookId,
                    ProjectId = project.Id,
                    SpaceId = project.SpaceId,
                    Name = "Rollback copy"
                }));
        });

        await Run<SquidDbContext>(async db =>
        {
            db.ChangeTracker.Clear();
            (await db.Set<Runbook>().CountAsync(r => r.ProjectId == project.Id)).ShouldBe(1);
            (await db.Set<DeploymentProcess>().CountAsync(
                p => p.ProjectId == project.Id
                     && p.ProcessKind == Squid.Message.Enums.DeploymentProcessKind.Runbook)).ShouldBe(1);
            (await db.Set<VariableSet>().CountAsync(
                v => v.SpaceId == project.SpaceId
                     && v.OwnerType == Squid.Message.Enums.VariableSetOwnerType.Runbook)).ShouldBe(1);
            (await db.Set<DeploymentStep>().CountAsync(s => s.ProcessId == sourceProcessId)).ShouldBe(1);
            (await db.Set<DeploymentAction>().CountAsync(a => a.Name == "Source action")).ShouldBe(1);
            (await db.Set<Runbook>().AnyAsync(r => r.Name == "Rollback copy")).ShouldBeFalse();
        });

        Observer.FailAtCopyStage = null;
    }

    private static async Task<ProjectSnapshot> SeedProjectAsync(SquidDbContext db)
    {
        var process = new DeploymentProcess
        {
            SpaceId = 1,
            ProcessKind = Squid.Message.Enums.DeploymentProcessKind.ProjectDeployment,
            OwnerId = null
        };
        db.Set<DeploymentProcess>().Add(process);
        await db.SaveChangesAsync();

        var variableSet = new VariableSet
        {
            Name = "Transaction Project",
            OwnerType = Squid.Message.Enums.VariableSetOwnerType.Project,
            OwnerId = 0,
            SpaceId = 1,
            RelatedDocumentIds = "[]"
        };
        db.Set<VariableSet>().Add(variableSet);
        await db.SaveChangesAsync();

        var project = new Squid.Core.Persistence.Entities.Deployments.Project
        {
            Name = "Transaction Project",
            Slug = "transaction-project",
            DeploymentProcessId = process.Id,
            VariableSetId = variableSet.Id,
            ProjectGroupId = 1,
            LifecycleId = 1,
            Json = string.Empty,
            IncludedLibraryVariableSetIds = "[]",
            SpaceId = 1
        };
        db.Set<Squid.Core.Persistence.Entities.Deployments.Project>().Add(project);
        await db.SaveChangesAsync();

        process.ProjectId = project.Id;
        variableSet.OwnerId = project.Id;
        await db.SaveChangesAsync();

        return new ProjectSnapshot(project.Id, project.SpaceId);
    }

    private sealed record ProjectSnapshot(int Id, int SpaceId);

    private sealed class FailAtStageObserver : IRunbookAggregateWriteObserver
    {
        public RunbookAggregateCreateStage? FailAt { get; set; }

        public RunbookAggregateCopyStage? FailAtCopyStage { get; set; }

        public int BeforeBusinessSaveCount { get; set; }

        public Task AfterCreateStageAsync(
            RunbookAggregateCreateStage stage,
            int runbookId,
            int processId,
            int variableSetId,
            CancellationToken cancellationToken = default)
        {
            if (FailAt == stage)
            {
                throw new InvalidOperationException($"Injected create failure at {stage}.");
            }

            return Task.CompletedTask;
        }

        public Task AfterCopyStageAsync(
            RunbookAggregateCopyStage stage,
            int runbookId,
            int processId,
            int variableSetId,
            CancellationToken cancellationToken = default)
        {
            if (FailAtCopyStage == stage)
            {
                throw new InvalidOperationException($"Injected copy failure at {stage}.");
            }

            return Task.CompletedTask;
        }

        public Task BeforeBusinessSaveAsync(
            RunbookAggregateCreateStage stage,
            CancellationToken cancellationToken = default)
        {
            BeforeBusinessSaveCount++;
            return Task.CompletedTask;
        }
    }
}
