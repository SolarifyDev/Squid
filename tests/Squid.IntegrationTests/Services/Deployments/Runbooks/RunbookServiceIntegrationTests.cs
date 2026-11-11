using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Runbooks;
using Squid.Core.Services.Deployments.Runbooks.Exceptions;
using Squid.Core.Services.Events;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.Deployments.Runbook;
using Squid.Message.Enums;
using Squid.Message.Enums.Events;
using Squid.Message.Models.Deployments.Runbook;
using Squid.Message.Requests.Events;
using Squid.Message.Requests.Deployments.Runbook;
using SquidProject = Squid.Core.Persistence.Entities.Deployments.Project;

namespace Squid.IntegrationTests.Services.Deployments.Runbooks;

public class RunbookServiceIntegrationTests : TestBase
{
    public RunbookServiceIntegrationTests()
        : base("_runbook_domain_service_v2_", "squid_it_runbook_domain_service_v2_20261105")
    {
    }

    [Fact]
    public async Task Create_PreallocatesOwnedAggregateAndRecordsOneCreatedAudit()
    {
        await Run<SquidDbContext>(async db =>
        {
            var project = await SeedProjectAsync(db);
            await Run<IRunbookService, SquidDbContext>(async (service, serviceDb) =>
            {
                var command = CreateCommand(project, "  Payment Recovery  ", "  handles failures  ", " recovery ", "RECOVERY", " ");
                var result = await service.CreateRunbookAsync(command);

                // The service and this check share the isolated request scope, so
                // the audit rows written after the business save are visible here.
                var runbookEvents = (await serviceDb.Set<Squid.Core.Persistence.Entities.Events.Event>()
                        .OrderBy(e => e.Id)
                        .ToListAsync())
                    .Where(e =>
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(e.ReferencesJson);
                        return doc.RootElement.TryGetProperty("DocumentType", out var documentType)
                               && documentType.GetString() == "Runbook";
                    })
                    .ToList();

                runbookEvents.Count(e => e.Category == EventCategory.DocumentCreated).ShouldBe(1);
                var createdEvent = runbookEvents.Single(e => e.Category == EventCategory.DocumentCreated);
                runbookEvents.Count(e => e.Category == EventCategory.DocumentModified).ShouldBe(0);
                createdEvent.ProjectId.ShouldBe(project.Id);
                AssertDocumentReference(createdEvent.ReferencesJson, "Runbook", result.Id, "Payment Recovery");

                db.ChangeTracker.Clear();

                var runbook = await db.Set<Runbook>().SingleAsync(r => r.Id == result.Id);
                var process = await db.Set<DeploymentProcess>().SingleAsync(p => p.Id == runbook.ProcessId);
                var variableSet = await db.Set<VariableSet>().SingleAsync(v => v.Id == runbook.VariableSetId);

                runbook.Name.ShouldBe("Payment Recovery");
                runbook.Description.ShouldBe("handles failures");
                runbook.TagsJson.ShouldBe("[\"recovery\"]");
                process.ProjectId.ShouldBe(project.Id);
                process.SpaceId.ShouldBe(project.SpaceId);
                process.ProcessKind.ShouldBe(DeploymentProcessKind.Runbook);
                process.OwnerId.ShouldBe(runbook.Id);
                variableSet.SpaceId.ShouldBe(project.SpaceId);
                variableSet.OwnerType.ShouldBe(VariableSetOwnerType.Runbook);
                variableSet.OwnerId.ShouldBe(runbook.Id);
                variableSet.Id.ShouldBe(runbook.VariableSetId);
                result.ProcessId.ShouldBe(runbook.ProcessId);
                result.Tags.ShouldBe(new[] { "recovery" });

                var projectProcess = await db.Set<DeploymentProcess>()
                    .SingleAsync(p => p.Id == project.DeploymentProcessId);
                projectProcess.ProcessKind.ShouldBe(DeploymentProcessKind.ProjectDeployment);
                projectProcess.OwnerId.ShouldBeNull();
                projectProcess.Id.ShouldNotBe(runbook.ProcessId);
            });
        });
    }

    [Fact]
    public async Task UpdateRecordsOneModifiedAuditAndPreservesIdentity()
    {
        await Run<SquidDbContext>(async db =>
        {
            var project = await SeedProjectAsync(db);

            await Run<IRunbookService, SquidDbContext>(async (service, serviceDb) =>
            {
                var created = await service.CreateRunbookAsync(CreateCommand(project, "Audit Before"));
                var updated = await service.UpdateRunbookAsync(new UpdateRunbookCommand
                {
                    Id = created.Id,
                    ProjectId = project.Id,
                    SpaceId = project.SpaceId,
                    Runbook = new UpdateRunbookModel
                    {
                        Name = "Audit After",
                        Tags = new List<string> { "audit" }
                    }
                });

                var runbookEvents = (await serviceDb.Set<Squid.Core.Persistence.Entities.Events.Event>()
                        .OrderBy(e => e.Id)
                        .ToListAsync())
                    .Where(e =>
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(e.ReferencesJson);
                        return doc.RootElement.TryGetProperty("DocumentType", out var documentType)
                               && documentType.GetString() == "Runbook";
                    })
                    .ToList();

                runbookEvents.Count(e => e.Category == EventCategory.DocumentCreated).ShouldBe(1);
                runbookEvents.Count(e => e.Category == EventCategory.DocumentModified).ShouldBe(1);
                var modifiedEvent = runbookEvents.Single(e => e.Category == EventCategory.DocumentModified);
                modifiedEvent.ProjectId.ShouldBe(project.Id);
                AssertDocumentReference(modifiedEvent.ReferencesJson, "Runbook", created.Id, "Audit After");
                updated.Id.ShouldBe(created.Id);
                updated.ProcessId.ShouldBe(created.ProcessId);
                updated.VariableSetId.ShouldBe(created.VariableSetId);
            });
        });
    }

    [Fact]
    public async Task Create_PreallocatesOwnedAggregateAndRecordsOneCreatedAudit_DetachedRead()
    {
        await Run<SquidDbContext>(async db =>
        {
            var project = await SeedProjectAsync(db);
            await Run<IRunbookService>(async service =>
            {
                await service.CreateRunbookAsync(CreateCommand(project, "Detached Read"));
            });

            db.ChangeTracker.Clear();
            var runbookEvents = (await db.Set<Squid.Core.Persistence.Entities.Events.Event>()
                    .OrderBy(e => e.Id)
                    .ToListAsync())
                .Where(e =>
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(e.ReferencesJson);
                    return doc.RootElement.TryGetProperty("DocumentType", out var documentType)
                           && documentType.GetString() == "Runbook";
                })
                .ToList();

            runbookEvents.Count(e => e.Category == EventCategory.DocumentCreated).ShouldBe(1);
        });
    }

    [Fact]
    public async Task Create_DuplicateNormalizedName_ThrowsStructuredConflictWithoutRows()
    {
        await Run<SquidDbContext>(async db =>
        {
            var project = await SeedProjectAsync(db);

            await Run<IRunbookService>(async service =>
            {
                await service.CreateRunbookAsync(CreateCommand(project, "Emergency Restore"));

                var exception = await Should.ThrowAsync<RunbookNameConflictException>(
                    () => service.CreateRunbookAsync(CreateCommand(project, "  emergency restore  ")));

                exception.Code.ShouldBe("runbook.name_conflict");
                exception.Field.ShouldBe("name");
            });

            db.ChangeTracker.Clear();
            (await db.Set<Runbook>().CountAsync(r => r.ProjectId == project.Id)).ShouldBe(1);
            (await db.Set<DeploymentProcess>().CountAsync(
                p => p.ProjectId == project.Id && p.ProcessKind == DeploymentProcessKind.Runbook)).ShouldBe(1);
            (await db.Set<VariableSet>().CountAsync(
                v => v.SpaceId == project.SpaceId && v.OwnerType == VariableSetOwnerType.Runbook)).ShouldBe(1);
        });
    }

    [Fact]
    public async Task Update_PreservesOwnedIdentityAndNormalizesMetadata()
    {
        await Run<SquidDbContext>(async db =>
        {
            var project = await SeedProjectAsync(db);

            await Run<IRunbookService>(async service =>
            {
                var created = await service.CreateRunbookAsync(CreateCommand(project, "Before"));
                var updated = await service.UpdateRunbookAsync(new UpdateRunbookCommand
                {
                    Id = created.Id,
                    ProjectId = project.Id,
                    SpaceId = project.SpaceId,
                    Runbook = new UpdateRunbookModel
                    {
                        Name = "  After  ",
                        Description = "  updated  ",
                        Tags = new List<string> { " ops ", "OPS" }
                    }
                });

                updated.Id.ShouldBe(created.Id);
                updated.ProcessId.ShouldBe(created.ProcessId);
                updated.VariableSetId.ShouldBe(created.VariableSetId);
                updated.Name.ShouldBe("After");
                updated.Description.ShouldBe("updated");
                updated.Tags.ShouldBe(new[] { "ops" });
            });
        });
    }

    [Fact]
    public async Task Update_DuplicateNormalizedName_ThrowsAndPreservesExistingMetadata()
    {
        await Run<SquidDbContext>(async db =>
        {
            var project = await SeedProjectAsync(db);

            await Run<IRunbookService>(async service =>
            {
                var first = await service.CreateRunbookAsync(CreateCommand(project, "First"));
                var second = await service.CreateRunbookAsync(CreateCommand(project, "Second"));

                var exception = await Should.ThrowAsync<RunbookNameConflictException>(
                    () => service.UpdateRunbookAsync(new UpdateRunbookCommand
                    {
                        Id = second.Id,
                        ProjectId = project.Id,
                        SpaceId = project.SpaceId,
                        Runbook = new UpdateRunbookModel
                        {
                            Name = "  first  "
                        }
                    }));

                exception.Code.ShouldBe("runbook.name_conflict");

                db.ChangeTracker.Clear();
                var persisted = await db.Set<Runbook>().SingleAsync(r => r.Id == second.Id);
                persisted.Name.ShouldBe("Second");
            });
        });
    }

    [Fact]
    public async Task Copy_DeepCopiesProcessChildrenAndLeavesSourceIndependent()
    {
        await Run<SquidDbContext>(async db =>
        {
            var project = await SeedProjectAsync(db);
            var sourceId = 0;

            await Run<IRunbookService>(async service =>
            {
                var sourceDto = await service.CreateRunbookAsync(CreateCommand(project, "Source Runbook"));
                sourceId = sourceDto.Id;

                var step = await SeedStepAsync(db, sourceDto.ProcessId, 1, "First");
                var action = await SeedActionAsync(db, step.Id, 1, "Script");
                await SeedActionChildRowsAsync(db, action.Id);
                await AddStepWithChildrenAsync(db, sourceDto.ProcessId);
                db.ChangeTracker.Clear();

                var copied = await service.CopyRunbookAsync(new CopyRunbookCommand
                {
                    SourceRunbookId = sourceDto.Id,
                    ProjectId = project.Id,
                    SpaceId = project.SpaceId
                });

                copied.Id.ShouldNotBe(sourceDto.Id);
                copied.ProcessId.ShouldNotBe(sourceDto.ProcessId);
                copied.VariableSetId.ShouldNotBe(sourceDto.VariableSetId);
                copied.Name.ShouldBe("Source Runbook (copy)");

                db.ChangeTracker.Clear();

                var sourceSteps = await db.Set<DeploymentStep>()
                    .Where(s => s.ProcessId == sourceDto.ProcessId)
                    .OrderBy(s => s.StepOrder)
                    .ToListAsync();
                var copiedSteps = await db.Set<DeploymentStep>()
                    .Where(s => s.ProcessId == copied.ProcessId)
                    .OrderBy(s => s.StepOrder)
                    .ToListAsync();
                copiedSteps.Count.ShouldBe(sourceSteps.Count);
                copiedSteps.Select(s => s.Name).ShouldBe(sourceSteps.Select(s => s.Name));
                copiedSteps.Select(s => s.Id).ShouldNotBe(sourceSteps.Select(s => s.Id));

                var sourceActions = await db.Set<DeploymentAction>()
                    .Where(a => sourceSteps.Select(s => s.Id).Contains(a.StepId))
                    .OrderBy(a => a.ActionOrder)
                    .ToListAsync();
                var copiedActions = await db.Set<DeploymentAction>()
                    .Where(a => copiedSteps.Select(s => s.Id).Contains(a.StepId))
                    .OrderBy(a => a.ActionOrder)
                    .ToListAsync();
                copiedActions.Count.ShouldBe(sourceActions.Count);
                copiedActions.Select(a => a.Name).ShouldBe(sourceActions.Select(a => a.Name));

                (await db.Set<DeploymentStepProperty>().CountAsync(
                    p => copiedSteps.Select(s => s.Id).Contains(p.StepId))).ShouldBe(1);
                (await db.Set<DeploymentActionProperty>().CountAsync(
                    p => copiedActions.Select(a => a.Id).Contains(p.ActionId))).ShouldBe(2);
                (await db.Set<ActionEnvironment>().CountAsync(
                    s => copiedActions.Select(a => a.Id).Contains(s.ActionId))).ShouldBe(2);
                (await db.Set<ActionExcludedEnvironment>().CountAsync(
                    s => copiedActions.Select(a => a.Id).Contains(s.ActionId))).ShouldBe(2);
                (await db.Set<ActionChannel>().CountAsync(
                    s => copiedActions.Select(a => a.Id).Contains(s.ActionId))).ShouldBe(2);
                (await db.Set<ActionMachineRole>().CountAsync(
                    s => copiedActions.Select(a => a.Id).Contains(s.ActionId))).ShouldBe(2);

                copiedSteps[0].Name = "Changed copy";
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();

                (await db.Set<DeploymentStep>().SingleAsync(s => s.Id == sourceSteps[0].Id))
                    .Name.ShouldBe("First");
            });

            db.ChangeTracker.Clear();
            var source = await db.Set<Runbook>().SingleAsync(r => r.Id == sourceId);
            var source1 = await db.Set<DeploymentStep>().SingleAsync(s => s.ProcessId == source.ProcessId && s.StepOrder == 1);
            source1.Name.ShouldBe("First");
        });
    }

    [Fact]
    public async Task Delete_RemovesOnlyOwnedAggregateAndChildren()
    {
        var projectId = 0;
        var sourceId = 0;

        await Run<SquidDbContext>(async db =>
        {
            var project = await SeedProjectAsync(db);
            projectId = project.Id;
            var projectProcessId = project.DeploymentProcessId;
            var projectVariableSetId = project.VariableSetId;
            var otherProject = await SeedProjectAsync(db, "Other Project", "other-project");
            var otherId = 0;

            await Run<IRunbookService>(async service =>
            {
                var source = await service.CreateRunbookAsync(CreateCommand(project, "Delete Me"));
                sourceId = source.Id;
                var other = await service.CreateRunbookAsync(CreateCommand(otherProject, "Keep Me"));
                otherId = other.Id;

                var sourceStep = await SeedStepAsync(db, source.ProcessId, 1, "Delete step");
                await SeedActionAsync(db, sourceStep.Id, 1, "Delete action");
                await SeedRunbookVariablesAsync(db, source.VariableSetId);
                var otherStep = await SeedStepAsync(db, other.ProcessId, 1, "Keep step");
                await SeedActionAsync(db, otherStep.Id, 1, "Keep action");
                db.ChangeTracker.Clear();

                var deleted = await service.DeleteRunbookAsync(new DeleteRunbookCommand
                {
                    Id = source.Id,
                    ProjectId = project.Id,
                    SpaceId = project.SpaceId
                });

                deleted.Id.ShouldBe(source.Id);
            });

            db.ChangeTracker.Clear();
            (await db.Set<Runbook>().AnyAsync(r => r.Id == sourceId)).ShouldBeFalse();
            (await db.Set<Runbook>().AnyAsync(r => r.Id == otherId)).ShouldBeTrue();
            (await db.Set<DeploymentProcess>().AnyAsync(p => p.Id == projectProcessId)).ShouldBeTrue();
            (await db.Set<VariableSet>().AnyAsync(v => v.Id == projectVariableSetId)).ShouldBeTrue();
            (await db.Set<DeploymentProcess>().AnyAsync(p => p.Id == project.DeploymentProcessId)).ShouldBeTrue();
            (await db.Set<VariableSet>().AnyAsync(v => v.Id == project.VariableSetId)).ShouldBeTrue();
            (await db.Set<DeploymentStep>().AnyAsync(s => s.Name == "Keep step")).ShouldBeTrue();
            (await db.Set<DeploymentStep>().AnyAsync(s => s.Name == "Delete step")).ShouldBeFalse();
            (await db.Set<DeploymentAction>().AnyAsync(a => a.Name == "Keep action")).ShouldBeTrue();
            (await db.Set<DeploymentAction>().AnyAsync(a => a.Name == "Delete action")).ShouldBeFalse();
            (await db.Set<Squid.Core.Persistence.Entities.Deployments.Variable>()
                .AnyAsync(v => v.Name == "Delete Variable")).ShouldBeFalse();
            (await db.Set<VariableScope>().AnyAsync(s => s.ScopeValue == "delete-scope")).ShouldBeFalse();
        });

        await Run<SquidDbContext>(async db =>
        {
            var deletedEvents = (await db.Set<Squid.Core.Persistence.Entities.Events.Event>()
                    .OrderByDescending(e => e.Id)
                    .ToListAsync())
                .Where(e =>
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(e.ReferencesJson);
                    return doc.RootElement.TryGetProperty("DocumentType", out var documentType)
                           && documentType.GetString() == "Runbook";
                })
                .ToList();

            deletedEvents.Count(e => e.Category == EventCategory.DocumentDeleted).ShouldBeGreaterThanOrEqualTo(1);
            var deletedEvent = deletedEvents.First(e => e.Category == EventCategory.DocumentDeleted);
            deletedEvent.ProjectId.ShouldBe(projectId);
            AssertDocumentReference(deletedEvent.ReferencesJson, "Runbook", sourceId, "Delete Me");
        });
    }

    [Fact]
    public async Task List_IsBoundedAndProjectScoped()
    {
        await Run<SquidDbContext>(async db =>
        {
            var project = await SeedProjectAsync(db);
            var otherProject = await SeedProjectAsync(db, "Other Project", "other-project");

            await Run<IRunbookService>(async service =>
            {
                await service.CreateRunbookAsync(CreateCommand(project, "Runbook A"));
                await service.CreateRunbookAsync(CreateCommand(project, "Runbook B"));
                await service.CreateRunbookAsync(CreateCommand(otherProject, "Runbook C"));

                var page = await service.GetRunbooksAsync(new GetRunbooksRequest
                {
                    ProjectId = project.Id,
                    SpaceId = project.SpaceId,
                    PageIndex = 1,
                    PageSize = 1
                });

                page.Count.ShouldBe(2);
                page.Runbooks.Count.ShouldBe(1);
                page.Runbooks[0].ProjectId.ShouldBe(project.Id);
            });
        });
    }

    private static CreateRunbookCommand CreateCommand(
        SquidProject project,
        string name,
        string description = null,
        params string[] tags)
    {
        return new CreateRunbookCommand
        {
            Runbook = new CreateRunbookModel
            {
                ProjectId = project.Id,
                SpaceId = project.SpaceId,
                Name = name,
                Description = description,
                Tags = tags.ToList()
            }
        };
    }

    private static void AssertDocumentReference(
        string referencesJson,
        string documentType,
        int documentId,
        string name)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(referencesJson);
        var root = doc.RootElement;

        root.GetProperty("DocumentType").GetString().ShouldBe(documentType);
        root.GetProperty("DocumentId").GetInt32().ShouldBe(documentId);
        root.GetProperty("Name").GetString().ShouldBe(name);
    }

    private static async Task<SquidProject> SeedProjectAsync(
        SquidDbContext db,
        string name = "Runbook Project",
        string slug = "runbook-project",
        int spaceId = 1)
    {
        var process = new DeploymentProcess
        {
            SpaceId = spaceId,
            ProcessKind = DeploymentProcessKind.ProjectDeployment,
            OwnerId = null
        };
        db.Set<DeploymentProcess>().Add(process);
        await db.SaveChangesAsync();

        var variableSet = new VariableSet
        {
            Name = name,
            OwnerType = VariableSetOwnerType.Project,
            OwnerId = 0,
            SpaceId = spaceId,
            RelatedDocumentIds = "[]"
        };
        db.Set<VariableSet>().Add(variableSet);
        await db.SaveChangesAsync();

        var project = new SquidProject
        {
            Name = name,
            Slug = slug,
            DeploymentProcessId = process.Id,
            VariableSetId = variableSet.Id,
            ProjectGroupId = 1,
            LifecycleId = 1,
            AutoCreateRelease = false,
            Json = string.Empty,
            IncludedLibraryVariableSetIds = "[]",
            SpaceId = spaceId
        };
        db.Set<SquidProject>().Add(project);
        await db.SaveChangesAsync();

        process.ProjectId = project.Id;
        variableSet.OwnerId = project.Id;
        await db.SaveChangesAsync();
        return project;
    }

    private static async Task<DeploymentStep> SeedStepAsync(
        SquidDbContext db,
        int processId,
        int order,
        string name)
    {
        var step = new DeploymentStep
        {
            ProcessId = processId,
            StepOrder = order,
            Name = name,
            StepType = "Action",
            Condition = "Success",
            StartTrigger = string.Empty,
            PackageRequirement = string.Empty
        };
        db.Set<DeploymentStep>().Add(step);
        await db.SaveChangesAsync();
        return step;
    }

    private static async Task<DeploymentAction> SeedActionAsync(
        SquidDbContext db,
        int stepId,
        int order,
        string name)
    {
        var action = new DeploymentAction
        {
            StepId = stepId,
            ActionOrder = order,
            Name = name,
            ActionType = "Octopus.Script",
            IsDisabled = false,
            IsRequired = true
        };
        db.Set<DeploymentAction>().Add(action);
        await db.SaveChangesAsync();
        return action;
    }

    private static async Task SeedActionChildRowsAsync(SquidDbContext db, int actionId)
    {
        db.Set<DeploymentActionProperty>().Add(new DeploymentActionProperty
        {
            ActionId = actionId,
            PropertyName = "Octopus.Action.Script.ScriptBody",
            PropertyValue = "Write-Host 'hello'"
        });
        db.Set<ActionEnvironment>().Add(new ActionEnvironment
        {
            ActionId = actionId,
            EnvironmentId = 1
        });
        db.Set<ActionExcludedEnvironment>().Add(new ActionExcludedEnvironment
        {
            ActionId = actionId,
            EnvironmentId = 2
        });
        db.Set<ActionChannel>().Add(new ActionChannel
        {
            ActionId = actionId,
            ChannelId = 1
        });
        db.Set<ActionMachineRole>().Add(new ActionMachineRole
        {
            ActionId = actionId,
            MachineRole = "web"
        });
        await db.SaveChangesAsync();
    }

    private static async Task AddStepWithChildrenAsync(SquidDbContext db, int processId)
    {
        var step = await SeedStepAsync(db, processId, 2, "Second");
        db.Set<DeploymentStepProperty>().Add(new DeploymentStepProperty
        {
            StepId = step.Id,
            PropertyName = "StepProperty",
            PropertyValue = "StepValue"
        });
        var action = await SeedActionAsync(db, step.Id, 1, "Second Script");
        await SeedActionChildRowsAsync(db, action.Id);
    }

    private static async Task SeedRunbookVariablesAsync(SquidDbContext db, int variableSetId)
    {
        var variable = new Squid.Core.Persistence.Entities.Deployments.Variable
        {
            VariableSetId = variableSetId,
            Name = "Delete Variable",
            Value = "delete-me",
            Type = Squid.Message.Enums.VariableType.String,
            SortOrder = 1
        };
        db.Set<Squid.Core.Persistence.Entities.Deployments.Variable>().Add(variable);
        await db.SaveChangesAsync();

        db.Set<VariableScope>().Add(new VariableScope
        {
            VariableId = variable.Id,
            ScopeType = Squid.Message.Enums.VariableScopeType.Environment,
            ScopeValue = "delete-scope"
        });
        await db.SaveChangesAsync();
    }
}
