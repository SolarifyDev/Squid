using System.Security.Cryptography;
using System.Text.Json;
using Autofac;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Core.Services.Security;
using Squid.Message.Commands.Deployments.Deployment;
using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using SquidEnvironment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Integration tier for the full-fidelity Octopus import: a Smarties-shaped export (library variable
/// set with encrypted sensitive values, encrypted feed password, approval team, Kubernetes containers
/// step with a disabled Custom-resource feature, step-template script) imported through the REAL
/// planning pipeline + confirmation orchestrator into a REAL isolated Postgres database, and — for the
/// deploy test — executed through the REAL deployment executor and local process transport.
/// </summary>
public class OctopusImportFullFidelityIntegrationTests : TestBase
{
    private const int SpaceId = 7;
    private const string ExportPassword = "sjfood-test-export";

    public OctopusImportFullFidelityIntegrationTests()
        : base("OctopusImportFullFidelity", "squid_it_octopus_import_full_fidelity")
    {
    }

    [Fact]
    public async Task SmartiesShapedExport_WithExportPassword_ImportsEveryConfigurationResourceFaithfully()
    {
        var sessionId = Guid.NewGuid();
        var archivePath = WriteArchive(SmartiesShapedDocuments(projectName: "Smarties.Api", libraryName: "Smarties", scriptBody: "Write-Host $BotUrl $OctopusParameters['Octopus.Project.Name']", syntax: "PowerShell"));

        try
        {
            await Run<ILifetimeScope>(async scope =>
            {
                var db = scope.Resolve<SquidDbContext>();
                var result = await ImportAsync(scope, db, sessionId, archivePath, OctopusExportSecrets.FromPassword(ExportPassword));

                result.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: Describe(result));

                var project = await db.Set<Project>().SingleAsync(p => p.SpaceId == SpaceId && p.Name == "Smarties.Api");
                var library = await db.Set<LibraryVariableSet>().SingleAsync(l => l.SpaceId == SpaceId && l.Name == "Smarties");
                var libraryVariableSet = await db.Set<VariableSet>().SingleAsync(v => v.Id == library.VariableSetId);
                var libraryVariables = await db.Set<Variable>().Where(v => v.VariableSetId == library.VariableSetId).ToListAsync();
                var testEnvironment = await db.Set<SquidEnvironment>().SingleAsync(e => e.SpaceId == SpaceId && e.Name == "TEST");

                project.GetIncludedLibraryVariableSetIdList().ShouldBe([library.Id]);
                libraryVariableSet.OwnerType.ShouldBe(VariableSetOwnerType.LibraryVariableSet);
                libraryVariableSet.OwnerId.ShouldBe(library.Id);
                libraryVariables.Select(v => v.Name).ShouldBe(["Authentication:Jwt:Issuer", "Email:Imap:Password", "Empty:Secret"], ignoreOrder: true);

                var issuer = libraryVariables.Single(v => v.Name == "Authentication:Jwt:Issuer");
                var issuerScopes = await db.Set<VariableScope>().Where(s => s.VariableId == issuer.Id).ToListAsync();
                issuerScopes.Single().ScopeType.ShouldBe(VariableScopeType.Environment);
                issuerScopes.Single().ScopeValue.ShouldBe(testEnvironment.Id.ToString());

                var imapPassword = libraryVariables.Single(v => v.Name == "Email:Imap:Password");
                imapPassword.IsSensitive.ShouldBeTrue();
                imapPassword.Value.ShouldNotBe("imap-p@ss'word", customMessage: "sensitive values must be encrypted at rest");
                (await scope.Resolve<IVariableEncryptionService>().DecryptAsync(imapPassword.Value, library.VariableSetId)).ShouldBe("imap-p@ss'word");
                libraryVariables.Single(v => v.Name == "Empty:Secret").IsSensitive.ShouldBeTrue();

                var feed = await db.Set<ExternalFeed>().AsNoTracking().SingleAsync(f => f.SpaceId == SpaceId && f.Name == "Yamimeal ACR");
                feed.Username.ShouldBe("yamimeal");
                feed.Password.ShouldNotBe("acr-registry-token", customMessage: "feed password must be encrypted at rest");
                scope.Resolve<IAtRestSecretProtector>().Unprotect(feed.Password, 0).ShouldBe("acr-registry-token");

                var team = await db.Set<Team>().SingleAsync(t => t.SpaceId == SpaceId && t.Name == "Solar");
                (await ActionPropertyAsync(db, "Approval", SpecialVariables.Action.ManualResponsibleTeamIds)).ShouldBe(team.Id.ToString());
                (await db.Set<TeamMember>().CountAsync(m => m.TeamId == team.Id)).ShouldBe(0, customMessage: "imported approval teams start empty (fail-closed) until an admin adds members");

                (await ActionPropertyAsync(db, "Deploy API", SpecialVariables.Action.KubernetesYaml)).ShouldBeNull("the Custom-resource feature was disabled in Octopus, so its YAML must not be deployed");
                (await ActionPropertyAsync(db, "Deploy API", "Squid.Action.KubernetesContainers.ObjectStatusCheck")).ShouldBe("True");

                var script = await ActionPropertyAsync(db, "Notify", SpecialVariables.Action.ScriptBody);
                script.ShouldStartWith("# --- Squid: Octopus compatibility (generated by Octopus import) ---");
                script.ShouldContain("$BotUrl = $OctopusParameters['BotUrl']");

                var projectVariable = await db.Set<Variable>().SingleAsync(v => v.VariableSetId == project.VariableSetId && v.Name == "ImageTag");
                projectVariable.Value.ShouldBe("api-#{Squid.Release.Number}");

                result.Result.Diagnostics.ShouldAllBe(d => !d.Message.Contains("imap-p@ss") && !d.Message.Contains("acr-registry-token"));
                result.Result.Diagnostics.Select(d => d.Code).ShouldNotContain(OctopusImportConfirmationDiagnosticCodes.ReusedLibraryVariableSetVariablesKept);
                result.Result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportConfirmationDiagnosticCodes.TeamCreatedWithoutMembers);
            });
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task SmartiesShapedExport_WithoutExportPassword_StaysFailClosedAndWritesNothing()
    {
        var archivePath = WriteArchive(SmartiesShapedDocuments(projectName: "Legacy.Api", libraryName: "Legacy Library", scriptBody: "Write-Host hi", syntax: "PowerShell"));

        try
        {
            await Run<ILifetimeScope>(async scope =>
            {
                var db = scope.Resolve<SquidDbContext>();
                var snapshot = await scope.Resolve<IOctopusImportPlanningPipeline>().BuildPreviewAsync(archivePath, SpaceId);

                snapshot.PreviewPlan.HasBlockers.ShouldBeTrue();
                snapshot.PreviewPlan.Diagnostics.Concat(snapshot.PreviewPlan.Resources.SelectMany(r => r.Diagnostics))
                    .Where(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker)
                    .Select(d => d.Code)
                    .Distinct()
                    .ShouldBe([OctopusImportPreviewDiagnosticCodes.RequiredSensitiveVariableInputMissing]);
                (await db.Set<Project>().AnyAsync(p => p.Name == "Legacy.Api")).ShouldBeFalse();
            });
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task SecondProjectSharingALibrary_ReusesTheExistingLibraryWithoutRewritingItsVariables()
    {
        var firstArchive = WriteArchive(SmartiesShapedDocuments(projectName: "First.Api", libraryName: "Shared Lib", scriptBody: "Write-Host 1", syntax: "PowerShell", teamName: "Team One", feedName: "Feed One"));
        var secondArchive = WriteArchive(SmartiesShapedDocuments(projectName: "Second.Api", libraryName: "Shared Lib", scriptBody: "Write-Host 2", syntax: "PowerShell", teamName: "Team One", feedName: "Feed One", issuerValue: "CHANGED-IN-SECOND-EXPORT"));

        try
        {
            await Run<ILifetimeScope>(async scope =>
            {
                var db = scope.Resolve<SquidDbContext>();
                (await ImportAsync(scope, db, Guid.NewGuid(), firstArchive, OctopusExportSecrets.FromPassword(ExportPassword))).State.ShouldBe(OctopusImportSessionState.Succeeded);
            });

            await Run<ILifetimeScope>(async scope =>
            {
                var db = scope.Resolve<SquidDbContext>();
                var result = await ImportAsync(scope, db, Guid.NewGuid(), secondArchive, OctopusExportSecrets.FromPassword(ExportPassword));

                result.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: Describe(result));

                var libraries = await db.Set<LibraryVariableSet>().Where(l => l.SpaceId == SpaceId && l.Name == "Shared Lib").ToListAsync();
                libraries.Count.ShouldBe(1);

                var first = await db.Set<Project>().SingleAsync(p => p.Name == "First.Api");
                var second = await db.Set<Project>().SingleAsync(p => p.Name == "Second.Api");
                second.GetIncludedLibraryVariableSetIdList().ShouldBe(first.GetIncludedLibraryVariableSetIdList());

                var issuer = await db.Set<Variable>().SingleAsync(v => v.VariableSetId == libraries[0].VariableSetId && v.Name == "Authentication:Jwt:Issuer");
                issuer.Value.ShouldBe("https://issuer.test", customMessage: "an existing shared library must never be rewritten by a later import");
                result.Result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportConfirmationDiagnosticCodes.ReusedLibraryVariableSetVariablesKept);
                (await db.Set<Team>().CountAsync(t => t.SpaceId == SpaceId && t.Name == "Team One")).ShouldBe(1);
            });
        }
        finally
        {
            File.Delete(firstArchive);
            File.Delete(secondArchive);
        }
    }

    [Fact]
    public async Task ImportedStepTemplateScript_DeploysThroughRealExecutor_WithLibrarySecretTemplateParameterAndOctopusBuiltIns()
    {
        if (OperatingSystem.IsWindows()) return;

        var markerPath = Path.Combine(Path.GetTempPath(), $"squid-octopus-fidelity-{Guid.NewGuid():N}.out");
        var scriptBody = "printf '%s|%s|%s|%s|%s' \"$(get_octopusvariable 'Octopus.Project.Name')\" \"$(get_octopusvariable 'Octopus.Release.Number')\" \"$(get_octopusvariable 'Octopus.Deployment.CreatedBy.Username')\" \"$(get_octopusvariable 'BotUrl')\" \"$(get_octopusvariable 'Email:Imap:Password')\" > '" + markerPath + "'";
        var archivePath = WriteArchive(SmartiesShapedDocuments(projectName: "Deployable.Api", libraryName: "Deployable Lib", scriptBody: scriptBody, syntax: "Bash", includeKubernetesStep: false, includeApproval: false, teamName: "Deploy Team", feedName: "Deploy Feed"));

        try
        {
            await Run<ILifetimeScope>(async scope =>
            {
                var db = scope.Resolve<SquidDbContext>();
                var result = await ImportAsync(scope, db, Guid.NewGuid(), archivePath, OctopusExportSecrets.FromPassword(ExportPassword));

                result.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: Describe(result));
            });

            await Run<SquidDbContext, IReleaseService, IDeploymentService, IDeploymentTaskExecutor>(async (db, releaseService, deploymentService, executor) =>
            {
                var project = await db.Set<Project>().SingleAsync(p => p.SpaceId == SpaceId && p.Name == "Deployable.Api");
                var environment = await db.Set<SquidEnvironment>().SingleAsync(e => e.SpaceId == SpaceId && e.Name == "TEST");
                var channel = await db.Set<Channel>().SingleAsync(c => c.ProjectId == project.Id && c.IsDefault);

                var release = await releaseService.CreateReleaseAsync(new CreateReleaseCommand { SpaceId = SpaceId, ProjectId = project.Id, ChannelId = channel.Id, Version = "2026.11.1" });
                var deployment = await deploymentService.CreateDeploymentAsync(new CreateDeploymentCommand { SpaceId = SpaceId, ReleaseId = release.Release.Id, EnvironmentId = environment.Id, Name = "Deploy imported template step" });

                await executor.ProcessAsync(deployment.TaskId, CancellationToken.None);

                db.ChangeTracker.Clear();
                (await db.Set<ServerTask>().AsNoTracking().SingleAsync(t => t.Id == deployment.TaskId)).State.ShouldBe(TaskState.Success);
            });

            File.Exists(markerPath).ShouldBeTrue("the imported step-template script must run through the real local process transport");
            File.ReadAllText(markerPath).ShouldBe("Deployable.Api|2026.11.1|internal_user|https://qyapi.example/send?key=k1&release=2026.11.1|imap-p@ss'word");
        }
        finally
        {
            File.Delete(archivePath);
            File.Delete(markerPath);
        }
    }

    [Fact]
    public async Task ExportPassword_IsRetainedEncryptedWhileActive_AndClearedWhenTheSessionTerminates()
    {
        var sessionId = Guid.NewGuid();

        await Run<ILifetimeScope>(async scope =>
        {
            var db = scope.Resolve<SquidDbContext>();
            db.Set<OctopusImportSession>().Add(Session(sessionId, OctopusImportSessionState.Uploaded));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var store = scope.Resolve<IOctopusImportExportSecretStore>();
            await store.StoreAsync(sessionId, SpaceId, ExportPassword);

            var stored = await db.Set<OctopusImportSession>().AsNoTracking().SingleAsync(s => s.SessionId == sessionId);
            stored.ProtectedExportPassword.ShouldNotBeNullOrEmpty();
            stored.ProtectedExportPassword.ShouldNotContain(ExportPassword);

            var secrets = await store.LoadAsync(sessionId, SpaceId);
            secrets.TryDecrypt(Encrypt("v", ExportPassword), out var value).ShouldBeTrue();
            value.ShouldBe("v");
        });

        await Run<ILifetimeScope>(async scope =>
        {
            var db = scope.Resolve<SquidDbContext>();
            var sessions = scope.Resolve<IOctopusImportSessionService>();
            var session = await db.Set<OctopusImportSession>().SingleAsync(s => s.SessionId == sessionId);
            session.State = OctopusImportSessionState.Importing.ToString();
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await sessions.RecordResultAsync(sessionId, SpaceId, OctopusImportSessionState.Failed, new());

            (await db.Set<OctopusImportSession>().AsNoTracking().SingleAsync(s => s.SessionId == sessionId)).ProtectedExportPassword.ShouldBeNull();
            (await scope.Resolve<IOctopusImportExportSecretStore>().LoadAsync(sessionId, SpaceId)).ShouldBeSameAs(OctopusExportSecrets.None);
        });
    }

    [Fact]
    public async Task ExpiredInterruptedAndPurgedSessions_DropTheRetainedExportPassword()
    {
        var expiredId = Guid.NewGuid();
        var purgedId = Guid.NewGuid();
        var interruptedId = Guid.NewGuid();

        await Run<ILifetimeScope>(async scope =>
        {
            var db = scope.Resolve<SquidDbContext>();
            var expired = Session(expiredId, OctopusImportSessionState.Previewed, protectedPassword: "v2:anything", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
            var purged = Session(purgedId, OctopusImportSessionState.Failed, protectedPassword: "v2:anything", uploadPath: "/tmp/purged.zip");
            var interrupted = Session(interruptedId, OctopusImportSessionState.Importing, protectedPassword: "v2:anything");
            interrupted.LastStateChangedAt = DateTimeOffset.UtcNow.AddHours(-2);
            db.Set<OctopusImportSession>().AddRange(expired, purged, interrupted);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var provider = scope.Resolve<IOctopusImportSessionDataProvider>();
            await provider.ExpireSessionsAsync(DateTimeOffset.UtcNow);
            await provider.MarkTemporaryUploadCleanedAsync(purgedId, "/tmp/purged.zip", DateTimeOffset.UtcNow);
            await provider.MarkInterruptedImportsFailedAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(-1), TimeSpan.FromHours(6));

            (await db.Set<OctopusImportSession>().AsNoTracking().SingleAsync(s => s.SessionId == expiredId)).ProtectedExportPassword.ShouldBeNull();
            (await db.Set<OctopusImportSession>().AsNoTracking().SingleAsync(s => s.SessionId == purgedId)).ProtectedExportPassword.ShouldBeNull();
            (await db.Set<OctopusImportSession>().AsNoTracking().SingleAsync(s => s.SessionId == interruptedId)).ProtectedExportPassword.ShouldBeNull();
        });
    }

    // ── Import driver ────────────────────────────────────────────────────────────────────────────

    private static async Task<Squid.Message.Models.OctopusImport.OctopusImportSessionDto> ImportAsync(ILifetimeScope scope, SquidDbContext db, Guid sessionId, string archivePath, OctopusExportSecrets secrets)
    {
        db.Set<OctopusImportSession>().Add(Session(sessionId, OctopusImportSessionState.Validated));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        scope.Resolve<IOctopusExportSecretsAccessor>().Secrets = secrets;

        var snapshot = await scope.Resolve<IOctopusImportPlanningPipeline>().BuildPreviewAsync(archivePath, SpaceId);
        snapshot.PreviewPlan.HasBlockers.ShouldBeFalse(customMessage: string.Join("; ", snapshot.PreviewPlan.Resources.SelectMany(r => r.Diagnostics).Concat(snapshot.PreviewPlan.Diagnostics).Where(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker).Select(d => $"{d.Code}:{d.SourceId}")));

        return await scope.Resolve<IOctopusImportConfirmationOrchestrator>().ConfirmAsync(new OctopusImportConfirmationRequest(sessionId, SpaceId, snapshot.Graph, snapshot.DependencyPlan, snapshot.PreviewPlan));
    }

    private static async Task<string> ActionPropertyAsync(SquidDbContext db, string actionName, string propertyName)
    {
        var action = await db.Set<DeploymentAction>().OrderByDescending(a => a.Id).FirstAsync(a => a.Name == actionName);

        return await db.Set<DeploymentActionProperty>().Where(p => p.ActionId == action.Id && p.PropertyName == propertyName).Select(p => p.PropertyValue).SingleOrDefaultAsync();
    }

    private static string Describe(Squid.Message.Models.OctopusImport.OctopusImportSessionDto result)
        => string.Join("; ", (result.Result?.Diagnostics ?? []).Where(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker).Select(d => $"{d.Code}:{d.SourceId}:{d.Message}"));

    private static OctopusImportSession Session(Guid sessionId, OctopusImportSessionState state, string protectedPassword = null, DateTimeOffset? expiresAt = null, string uploadPath = null)
        => new()
        {
            SessionId = sessionId,
            DestinationSpaceId = SpaceId,
            OwnerUserId = CurrentUsers.InternalUser.Id,
            State = state.ToString(),
            SourceSummaryJson = "{}",
            DataVersion = Guid.NewGuid().ToByteArray(),
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddHours(1),
            LastStateChangedAt = DateTimeOffset.UtcNow,
            ProtectedExportPassword = protectedPassword,
            TemporaryUploadPath = uploadPath
        };

    // ── Smarties-shaped export ───────────────────────────────────────────────────────────────────

    private static List<(string Id, string DocumentType, string DocumentSource, string Json)> SmartiesShapedDocuments(string projectName, string libraryName, string scriptBody, string syntax, bool includeKubernetesStep = true, bool includeApproval = true, string teamName = "Solar", string feedName = "Yamimeal ACR", string issuerValue = "https://issuer.test")
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        string Id(string prefix) => $"{prefix}-{suffix}";

        var steps = new List<object>();

        if (includeApproval)
            steps.Add(Step(Id("Steps-approval"), "Approval", Action(Id("Actions-approval"), "Approval", "Octopus.Manual", new Dictionary<string, string>
            {
                ["Octopus.Action.Manual.Instructions"] = "Approve the release",
                ["Octopus.Action.Manual.ResponsibleTeamIds"] = Id("Teams-286"),
                ["Octopus.Action.RunOnServer"] = "true"
            })));

        if (includeKubernetesStep)
            steps.Add(Step(Id("Steps-k8s"), "Deploy API", Action(Id("Actions-k8s"), "Deploy API", "Octopus.KubernetesDeployContainers", new Dictionary<string, string>
            {
                ["Octopus.Action.EnabledFeatures"] = "Octopus.Features.KubernetesService,Octopus.Features.KubernetesIngress,Octopus.Features.KubernetesConfigMap,Octopus.Features.KubernetesSecret",
                ["Octopus.Action.KubernetesContainers.DeploymentResourceType"] = "Deployment",
                ["Octopus.Action.KubernetesContainers.DeploymentName"] = "smarties-api",
                ["Octopus.Action.KubernetesContainers.Namespace"] = "#{NameSpace}",
                ["Octopus.Action.KubernetesContainers.DeploymentWait"] = "Wait",
                ["Octopus.Action.KubernetesContainers.PodManagementPolicy"] = "OrderedReady",
                ["Octopus.Action.KubernetesContainers.CustomResourceYaml"] = "apiVersion: v1\ndata:\n#{each var in OctopusParameters}\n  #{var.Key}: x\n#{/each}"
            }), new Dictionary<string, string> { ["Octopus.Action.TargetRoles"] = "k8s" }));

        steps.Add(Step(Id("Steps-notify"), "Notify", Action(Id("Actions-notify"), "Notify", "Octopus.Script", new Dictionary<string, string>
        {
            ["Octopus.Action.Script.ScriptSource"] = "Inline",
            ["Octopus.Action.Script.Syntax"] = syntax,
            ["Octopus.Action.Script.ScriptBody"] = scriptBody,
            ["Octopus.Action.Template.Id"] = Id("ActionTemplates-442"),
            ["Octopus.Action.Template.Version"] = "6",
            ["Octopus.Action.RunOnServer"] = "true",
            ["BotUrl"] = "https://qyapi.example/send?key=k1&release=#{Octopus.Release.Number}"
        })));

        return
        [
            Doc(Id("ProjectGroups-181"), "ProjectGroup", new { Id = Id("ProjectGroups-181"), Name = $"AI {suffix}", Slug = $"ai-{suffix}" }),
            Doc(Id("Environments-2"), "StaticDeploymentEnvironment", new { Id = Id("Environments-2"), Name = "TEST", Slug = "test" }),
            Doc(Id("Environments-3"), "StaticDeploymentEnvironment", new { Id = Id("Environments-3"), Name = "PRD", Slug = "prd" }),
            Doc(Id("Lifecycles-302"), "Lifecycle", new { Id = Id("Lifecycles-302"), Name = $"AI {suffix}", Slug = $"ai-lc-{suffix}", Phases = new[] { new { Id = Id("Phases-1"), Name = "Test", AutomaticDeploymentTargets = new[] { Id("Environments-2") }, OptionalDeploymentTargets = new[] { Id("Environments-3") } } } }),
            Doc(Id("Feeds-1061"), "DockerFeed", new { Id = Id("Feeds-1061"), Name = feedName, Slug = feedName.ToLowerInvariant().Replace(' ', '-'), FeedType = "Docker", FeedUri = "https://yamimeal.azurecr.io", ApiVersion = "v2", Username = "yamimeal", Password = Encrypt("acr-registry-token", ExportPassword) }),
            Doc(Id("Teams-286"), "ConfigurableTeam", new { Id = Id("Teams-286"), Name = teamName, Description = "", MemberUserIds = new[] { "Users-1", "Users-2" }, ExternalSecurityGroups = Array.Empty<string>() }),
            Doc(Id("WorkerPools-1"), "StaticWorkerPool", new { Id = Id("WorkerPools-1"), Name = "Default Worker Pool" }),
            Doc(Id("ActionTemplates-442"), "ActionTemplate", new { Id = Id("ActionTemplates-442"), Name = "Send deployment wechat bot notification", ActionType = "Octopus.Script", Version = 6, Properties = new Dictionary<string, string> { ["Octopus.Action.Script.Syntax"] = syntax }, Parameters = new[] { new { Id = "p1", Name = "BotUrl", Label = "Bot url", DefaultValue = "", DisplaySettings = new Dictionary<string, string> { ["Octopus.ControlType"] = "SingleLineText" } } } }),
            Doc(Id("LibraryVariableSets-141"), "LibraryVariableSet", new { Id = Id("LibraryVariableSets-141"), Name = libraryName, Description = "", VariableSetId = Id("variableset-LibraryVariableSets-141"), ContentType = "Variables", Templates = Array.Empty<object>() }),
            Doc(Id("variableset-LibraryVariableSets-141"), "LibraryVariables", new
            {
                Id = Id("variableset-LibraryVariableSets-141"),
                OwnerId = Id("LibraryVariableSets-141"),
                OwnerType = "LibraryVariableSet",
                Variables = new object[]
                {
                    new { Id = Guid.NewGuid().ToString(), Name = "Authentication:Jwt:Issuer", Value = issuerValue, Scope = new Dictionary<string, string[]> { ["Environment"] = [Id("Environments-2")] } },
                    new { Id = Guid.NewGuid().ToString(), Name = "Email:Imap:Password", Type = "Sensitive", Value = Encrypt("imap-p@ss'word", ExportPassword), Scope = new Dictionary<string, string[]> { ["Environment"] = [Id("Environments-2")] } },
                    new { Id = Guid.NewGuid().ToString(), Name = "Empty:Secret", Type = "Sensitive", Value = (string)null, Scope = new Dictionary<string, string[]>() }
                }
            }),
            Doc(Id("Projects-643"), "Project", new { Id = Id("Projects-643"), Name = projectName, Slug = projectName.ToLowerInvariant().Replace('.', '-'), ProjectGroupId = Id("ProjectGroups-181"), LifecycleId = Id("Lifecycles-302"), VariableSetId = Id("variableset-Projects-643"), DeploymentProcessId = Id("deploymentprocess-Projects-643"), IncludedLibraryVariableSetIds = new[] { Id("LibraryVariableSets-141") } }),
            Doc(Id("Channels-803"), "Channel", new { Id = Id("Channels-803"), Name = "Default", ProjectId = Id("Projects-643"), LifecycleId = (string)null, IsDefault = true, Slug = "default" }),
            Doc(Id("variableset-Projects-643"), "ProjectVariables", new
            {
                Id = Id("variableset-Projects-643"),
                OwnerId = Id("Projects-643"),
                OwnerType = "Project",
                Variables = new object[]
                {
                    new { Id = Guid.NewGuid().ToString(), Name = "ImageTag", Value = "api-#{Octopus.Release.Number}", Scope = new Dictionary<string, string[]>() },
                    new { Id = Guid.NewGuid().ToString(), Name = "NameSpace", Value = "smarties", Scope = new Dictionary<string, string[]>() }
                }
            }),
            Doc(Id("deploymentprocess-Projects-643"), "DeploymentProcess", new { Id = Id("deploymentprocess-Projects-643"), OwnerId = Id("Projects-643"), Steps = steps })
        ];
    }

    private static object Step(string id, string name, object action, Dictionary<string, string> stepProperties = null)
        => new { Id = id, Name = name, Condition = "Success", StartTrigger = "StartAfterPrevious", PackageRequirement = "LetOctopusDecide", Properties = stepProperties ?? new Dictionary<string, string>(), Actions = new[] { action } };

    private static object Action(string id, string name, string actionType, Dictionary<string, string> properties)
        => new { Id = id, Name = name, ActionType = actionType, IsRequired = false, IsDisabled = false, Environments = Array.Empty<string>(), ExcludedEnvironments = Array.Empty<string>(), Channels = Array.Empty<string>(), Packages = Array.Empty<object>(), Properties = properties };

    private static (string Id, string DocumentType, string DocumentSource, string Json) Doc(string id, string documentType, object document)
        => (id, documentType, $"{id}.json", JsonSerializer.Serialize(document));

    private static string WriteArchive(IReadOnlyList<(string Id, string DocumentType, string DocumentSource, string Json)> documents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"squid-octopus-fidelity-{Guid.NewGuid():N}.zip");
        var manifest = JsonSerializer.Serialize(new
        {
            SchemaVersions = Array.Empty<string>(),
            Entries = documents.Select(d => new { d.Id, Name = d.Id, d.DocumentType, ExportType = "FullDocument", d.DocumentSource, ParentId = (string)null, Hash = Sha1(d.Json) })
        });

        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);

        WriteEntry(archive, "manifest.json", manifest);

        foreach (var document in documents)
            WriteEntry(archive, document.DocumentSource, document.Json);

        return path;
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        using var stream = archive.CreateEntry(path).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    private static string Sha1(string value) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>Independent reference implementation of the Octopus export cipher (not shared with production code).</summary>
    private static string Encrypt(string plaintext, string password)
    {
        var key = Rfc2898DeriveBytes.Pbkdf2(password, Encoding.UTF8.GetBytes("Octopuss"), 1_000, HashAlgorithmName.SHA1, 16);
        var iv = RandomNumberGenerator.GetBytes(16);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;

        using var encryptor = aes.CreateEncryptor();
        var bytes = Encoding.UTF8.GetBytes(plaintext);

        return $"{Convert.ToBase64String(encryptor.TransformFinalBlock(bytes, 0, bytes.Length))}|{Convert.ToBase64String(iv)}";
    }
}
