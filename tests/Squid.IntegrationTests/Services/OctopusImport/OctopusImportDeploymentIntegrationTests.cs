using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Moq;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Commands.Deployments.Deployment;
using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using SquidEnvironment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.IntegrationTests.Services.OctopusImport;

public class OctopusImportDeploymentIntegrationTests : TestBase
{
    private const int SpaceId = 7;

    public OctopusImportDeploymentIntegrationTests()
        : base("OctopusImportDeployment", "squid_it_octopus_import_deployment")
    {
    }

    [Fact]
    public async Task ArchiveImport_ImportedProjectProcessAndScopedVariables_CanCompleteDeployment()
    {
        var sessionId = Guid.NewGuid();
        var archivePath = CreateDeployableProjectArchive();

        try
        {
            await Run<SquidDbContext, IOctopusImportPlanningPipeline, IOctopusImportConfirmationOrchestrator>(
                async (db, pipeline, orchestrator) =>
                {
                    db.Set<OctopusImportSession>().Add(CreateSession(sessionId));
                    await db.SaveChangesAsync();
                    db.ChangeTracker.Clear();

                    var snapshot = await pipeline.BuildPreviewAsync(archivePath, SpaceId);
                    snapshot.Graph.Diagnostics.ShouldNotContain(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);
                    snapshot.DependencyPlan.Diagnostics.ShouldNotContain(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);
                    snapshot.PreviewPlan.HasBlockers.ShouldBeFalse();

                    var result = await orchestrator.ConfirmAsync(new OctopusImportConfirmationRequest(
                        sessionId,
                        SpaceId,
                        snapshot.Graph,
                        snapshot.DependencyPlan,
                        snapshot.PreviewPlan));

                    result.State.ShouldBe(OctopusImportSessionState.Succeeded);
                    result.Result.Succeeded.ShouldBeTrue();

                    var project = await db.Set<Project>()
                        .SingleAsync(p => p.SpaceId == SpaceId && p.Name == "Imported Deployable Project");
                    var importedAction = await db.Set<DeploymentAction>()
                        .SingleAsync(action => action.Name == "Imported script action");
                    var importedVariable = await db.Set<Variable>()
                        .SingleAsync(variable => variable.VariableSetId == project.VariableSetId && variable.Name == "Scoped variable");
                    var scopes = await db.Set<VariableScope>()
                        .Where(scope => scope.VariableId == importedVariable.Id)
                        .ToListAsync();
                    var actionMapping = result.Result.IdMappings.Single(mapping =>
                        mapping.SourceType == OctopusResourceKind.DeploymentAction.ToString() &&
                        mapping.SourceId == "Actions-1");
                    var processMapping = result.Result.IdMappings.Single(mapping =>
                        mapping.SourceType == OctopusResourceKind.DeploymentProcess.ToString() &&
                        mapping.SourceId == "deploymentprocess-Projects-1");

                    actionMapping.DestinationId.ShouldBe(importedAction.Id);
                    processMapping.DestinationId.ShouldBe(project.DeploymentProcessId);
                    scopes
                        .Select(scope => (scope.ScopeType, scope.ScopeValue))
                        .ShouldBe([
                            (VariableScopeType.Action, importedAction.Id.ToString()),
                            (VariableScopeType.Process, project.DeploymentProcessId.ToString())
                        ], ignoreOrder: true);
                });
        }
        finally
        {
            File.Delete(archivePath);
        }

        var executionStrategy = new Mock<IExecutionStrategy>();
        executionStrategy
            .Setup(strategy => strategy.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScriptExecutionResult
            {
                Success = true,
                ExitCode = 0,
                LogLines = []
            });

        await Run<SquidDbContext, IReleaseService, IDeploymentService, IDeploymentTaskExecutor>(
            async (db, releaseService, deploymentService, executor) =>
            {
                var project = await db.Set<Project>()
                    .SingleAsync(p => p.SpaceId == SpaceId && p.Name == "Imported Deployable Project");
                var environment = await db.Set<SquidEnvironment>()
                    .SingleAsync(e => e.SpaceId == SpaceId && e.Name == "Imported Production");
                var channel = await db.Set<Channel>()
                    .SingleAsync(c => c.ProjectId == project.Id && c.IsDefault);
                var importedStep = await db.Set<DeploymentStep>()
                    .SingleAsync(step => step.ProcessId == project.DeploymentProcessId);
                var importedAction = await db.Set<DeploymentAction>()
                    .SingleAsync(action => action.StepId == importedStep.Id);
                var importedScriptBody = await db.Set<DeploymentActionProperty>()
                    .SingleAsync(property =>
                        property.ActionId == importedAction.Id &&
                        property.PropertyName == SpecialVariables.Action.ScriptBody);

                importedStep.Name.ShouldBe("Imported script step");
                importedAction.Name.ShouldBe("Imported script action");
                importedAction.ActionType.ShouldBe(SpecialVariables.ActionTypes.Script);
                importedScriptBody.PropertyValue.ShouldBe("echo imported-project-deployed");

                var releaseEvent = await releaseService.CreateReleaseAsync(new CreateReleaseCommand
                {
                    SpaceId = SpaceId,
                    ProjectId = project.Id,
                    ChannelId = channel.Id,
                    Version = "1.0.0"
                });
                var release = await db.Set<Release>()
                    .SingleAsync(candidate => candidate.Id == releaseEvent.Release.Id);

                var deploymentEvent = await deploymentService.CreateDeploymentAsync(new CreateDeploymentCommand
                {
                    SpaceId = SpaceId,
                    ReleaseId = release.Id,
                    EnvironmentId = environment.Id,
                    Name = "Deploy imported project"
                });

                await executor.ProcessAsync(deploymentEvent.TaskId, CancellationToken.None);

                db.ChangeTracker.Clear();
                var completedTask = await db.Set<ServerTask>()
                    .AsNoTracking()
                    .SingleAsync(task => task.Id == deploymentEvent.TaskId);
                var completion = await db.Set<DeploymentCompletion>()
                    .AsNoTracking()
                    .SingleAsync(candidate => candidate.DeploymentId == deploymentEvent.Deployment.Id);

                completedTask.State.ShouldBe(TaskState.Success);
                completion.State.ShouldBe(TaskState.Success);
            },
            builder => RegisterDeploymentDependencies(builder, executionStrategy.Object));

        executionStrategy.Verify(
            strategy => strategy.ExecuteScriptAsync(
                It.Is<ScriptExecutionRequest>(request => request.ScriptBody == "echo imported-project-deployed"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static OctopusImportSession CreateSession(Guid sessionId)
    {
        return new OctopusImportSession
        {
            SessionId = sessionId,
            DestinationSpaceId = SpaceId,
            OwnerUserId = CurrentUsers.InternalUser.Id,
            State = OctopusImportSessionState.Validated.ToString(),
            SourceSummaryJson = "{}",
            DataVersion = Guid.NewGuid().ToByteArray(),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            LastStateChangedAt = DateTimeOffset.UtcNow
        };
    }

    private static string CreateDeployableProjectArchive()
    {
        var documents = new[]
        {
            Document(
                "ProjectGroups-1",
                "ProjectGroup",
                "ProjectGroups-1.json",
                """{"Id":"ProjectGroups-1","Name":"Imported Group","Slug":"imported-group"}"""),
            Document(
                "Environments-1",
                "StaticDeploymentEnvironment",
                "Environments-1.json",
                """{"Id":"Environments-1","Name":"Imported Production","Slug":"imported-production"}"""),
            Document(
                "Lifecycles-1",
                "Lifecycle",
                "Lifecycles-1.json",
                """
                {
                  "Id":"Lifecycles-1",
                  "Name":"Imported Lifecycle",
                  "Slug":"imported-lifecycle",
                  "Phases":[{
                    "Id":"Phases-1",
                    "Name":"Production",
                    "AutomaticDeploymentTargets":["Environments-1"]
                  }]
                }
                """),
            Document(
                "Projects-1",
                "Project",
                "Projects-1.json",
                """
                {
                  "Id":"Projects-1",
                  "Name":"Imported Deployable Project",
                  "Slug":"imported-deployable-project",
                  "ProjectGroupId":"ProjectGroups-1",
                  "LifecycleId":"Lifecycles-1",
                  "VariableSetId":"variableset-Projects-1",
                  "DeploymentProcessId":"deploymentprocess-Projects-1"
                }
                """),
            Document(
                "variableset-Projects-1",
                "ProjectVariables",
                "variableset-Projects-1.json",
                """
                {
                  "Id":"variableset-Projects-1",
                  "OwnerId":"Projects-1",
                  "OwnerType":"Project",
                  "Variables":[{
                    "Id":"Variables-1",
                    "Name":"Scoped variable",
                    "Value":"scoped-value",
                    "Type":"String",
                    "Scope":{
                      "Action":["Actions-1"],
                      "Process":["deploymentprocess-Projects-1"]
                    }
                  }]
                }
                """),
            Document(
                "deploymentprocess-Projects-1",
                "DeploymentProcess",
                "deploymentprocess-Projects-1.json",
                """
                {
                  "Id":"deploymentprocess-Projects-1",
                  "OwnerId":"Projects-1",
                  "Steps":[{
                    "Id":"Steps-1",
                    "Name":"Imported script step",
                    "Condition":"Success",
                    "StartTrigger":"StartAfterPrevious",
                    "Actions":[{
                      "Id":"Actions-1",
                      "Name":"Imported script action",
                      "ActionType":"Octopus.Script",
                      "IsRequired":true,
                      "Properties":{
                        "Octopus.Action.RunOnServer":"true",
                        "Octopus.Action.Script.ScriptSource":"Inline",
                        "Octopus.Action.Script.Syntax":"Bash",
                        "Octopus.Action.Script.ScriptBody":"echo imported-project-deployed"
                      }
                    }]
                  }]
                }
                """)
        };

        var path = Path.Combine(Path.GetTempPath(), $"squid-octopus-import-deployment-{Guid.NewGuid():N}.zip");
        var manifest = JsonSerializer.Serialize(new
        {
            SchemaVersions = Array.Empty<string>(),
            Entries = documents.Select(document => new
            {
                document.Id,
                Name = document.Id,
                document.DocumentType,
                ExportType = "FullDocument",
                document.DocumentSource,
                ParentId = (string)null,
                Hash = Sha1(document.Json)
            })
        });

        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        WriteArchiveEntry(archive, "manifest.json", manifest);
        foreach (var document in documents)
            WriteArchiveEntry(archive, document.DocumentSource, document.Json);

        return path;
    }

    private static (string Id, string DocumentType, string DocumentSource, string Json) Document(
        string id,
        string documentType,
        string documentSource,
        string json)
        => (id, documentType, documentSource, json);

    private static void WriteArchiveEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    private static string Sha1(string value)
    {
        var hash = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void RegisterDeploymentDependencies(ContainerBuilder builder, IExecutionStrategy executionStrategy)
    {
        var transport = new Mock<IDeploymentTransport>();
        transport.Setup(candidate => candidate.CommunicationStyle).Returns(CommunicationStyle.None);
        transport.Setup(candidate => candidate.Strategy).Returns(executionStrategy);
        transport.Setup(candidate => candidate.Capabilities).Returns(ServerTransport.Capability);

        var registry = new Mock<ITransportRegistry>();
        registry.Setup(candidate => candidate.Resolve(CommunicationStyle.None)).Returns(transport.Object);

        builder.RegisterInstance(registry.Object)
            .As<ITransportRegistry>()
            .SingleInstance();
    }
}
