using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping.Actions;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Message.Commands.Deployments.Deployment;
using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using SquidEnvironment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Integration tier for the Octopus script compatibility preamble: a step-template Bash script imported
/// through the REAL planning pipeline + confirmation orchestrator into a REAL isolated Postgres database,
/// then deployed through the REAL deployment executor and local process transport, so the values the script
/// reads have gone through the real variable resolver (project + environment-scoped library variables),
/// the real creator-variable lookup and Squid's real two-pass substitution.
/// </summary>
public class OctopusImportScriptCompatibilityIntegrationTests : TestBase
{
    private const int SpaceId = 9;
    private const string ExportPassword = "fake-script-compat-export";
    private const string LibrarySecret = "imap-p@ss'word";
    private const int DeletedUserId = 987_654;

    public OctopusImportScriptCompatibilityIntegrationTests()
        : base("OctopusImportScriptCompatibility", "squid_it_octopus_import_script_compat")
    {
    }

    [Fact]
    public async Task ImportedScript_DeployedPerEnvironment_ReadsScopedValuesSiblingParametersAndHostileValuesExactly()
    {
        if (OperatingSystem.IsWindows()) return;

        var outputDirectory = Directory.CreateTempSubdirectory("squid-octopus-script-compat-").FullName;
        var injectedMarker = Path.Combine(outputDirectory, "injected");
        var hostileNote = $"line1\nSQUID_OCTOPUS_VALUE\ntouch '{injectedMarker}'\n'@\nit's `id` $(id) \"q\" 你好";
        var archivePath = WriteArchive(Documents(ScriptBody(outputDirectory), hostileNote, includeTemplate: true, extraActionProperties: null));

        try
        {
            await ImportAsync(archivePath, expectBlockers: false);

            var deployerId = await CreateUserAsync("compat.deployer", "Compat Deployer");
            var releaseId = await CreateReleaseAsync("2026.11.7");

            await DeployAsync(releaseId, "TEST", deployerId);
            await DeployAsync(releaseId, "PRD", deployerId);

            File.ReadAllText(Path.Combine(outputDirectory, "out-TEST.txt")).ShouldBe($"{LibrarySecret}|{deployerId}|compat.deployer|Compat Deployer|https://hook.example/send?release=2026.11.7|{hostileNote}", customMessage: "TEST: the TEST-scoped library secret, the creator, the chained template parameters and the hostile value must all arrive verbatim");
            File.ReadAllText(Path.Combine(outputDirectory, "out-PRD.txt")).ShouldBe($"|{deployerId}|compat.deployer|Compat Deployer|https://hook.example/send?release=2026.11.7|{hostileNote}", customMessage: "PRD: the library secret is scoped to TEST only, so on PRD it must read as empty (Octopus semantics), never as the literal #{...} token");

            await DeployAsync(releaseId, "TEST", DeletedUserId);

            File.ReadAllText(Path.Combine(outputDirectory, "out-TEST.txt")).ShouldBe($"{LibrarySecret}|{DeletedUserId}|||https://hook.example/send?release=2026.11.7|{hostileNote}", customMessage: "a deployment whose triggering user no longer exists still defines Squid.Deployment.CreatedBy.* (id, empty names)");
            File.Exists(injectedMarker).ShouldBeFalse("a deploy-time value containing heredoc / here-string terminators must never execute as script code");
        }
        finally
        {
            File.Delete(archivePath);
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task EncryptedTemplateParameterWhoseTemplateIsMissing_BlocksTheImportAndNeverPersistsTheCiphertext()
    {
        var ciphertext = Encrypt("fake-api-key", ExportPassword);
        var cipherPart = ciphertext.Split('|')[0];
        var archivePath = WriteArchive(Documents("echo hi", "note", includeTemplate: false, extraActionProperties: new Dictionary<string, string> { ["ApiKey"] = ciphertext }));

        try
        {
            await Run<ILifetimeScope>(async scope =>
            {
                var db = scope.Resolve<SquidDbContext>();
                var sessionId = Guid.NewGuid();

                db.Set<OctopusImportSession>().Add(Session(sessionId));
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();

                scope.Resolve<IOctopusExportSecretsAccessor>().Secrets = OctopusExportSecrets.FromPassword(ExportPassword);

                // The action-mapper blocker may surface at preview or at confirm; either way the import must not succeed.
                var snapshot = await scope.Resolve<IOctopusImportPlanningPipeline>().BuildPreviewAsync(archivePath, SpaceId);
                var diagnostics = snapshot.PreviewPlan.Diagnostics.Concat(snapshot.PreviewPlan.Resources.SelectMany(r => r.Diagnostics)).ToList();

                if (!snapshot.PreviewPlan.HasBlockers)
                {
                    var result = await scope.Resolve<IOctopusImportConfirmationOrchestrator>().ConfirmAsync(new OctopusImportConfirmationRequest(sessionId, SpaceId, snapshot.Graph, snapshot.DependencyPlan, snapshot.PreviewPlan));

                    result.State.ShouldNotBe(OctopusImportSessionState.Succeeded);
                    diagnostics.AddRange(result.Result?.Diagnostics ?? []);
                }

                diagnostics.ShouldContain(d => d.Code == OctopusImportActionMappingDiagnosticCodes.SensitiveStepTemplateParameter && d.Severity == OctopusImportCompatibilitySeverity.Blocker);
                diagnostics.ShouldAllBe(d => !d.Message.Contains(cipherPart));

                db.ChangeTracker.Clear();
                (await db.Set<DeploymentActionProperty>().AnyAsync(p => p.PropertyValue.Contains(cipherPart))).ShouldBeFalse("the export ciphertext must never be written into a script body");
            });
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    // ── Drivers ──────────────────────────────────────────────────────────────────────────────────

    private static string ScriptBody(string outputDirectory)
        => "{ printf '%s|' \"$(get_octopusvariable 'Email:Imap:Password')\" \"$(get_octopusvariable 'Octopus.Deployment.CreatedBy.Id')\" \"$(get_octopusvariable 'Octopus.Deployment.CreatedBy.Username')\" \"$(get_octopusvariable 'Octopus.Deployment.CreatedBy.DisplayName')\" \"$(get_octopusvariable BotUrl)\"; get_octopusvariable 'App:Note'; } > '"
           + outputDirectory + "/out-#{Octopus.Environment.Name}.txt'";

    private async Task ImportAsync(string archivePath, bool expectBlockers)
    {
        await Run<ILifetimeScope>(async scope =>
        {
            var db = scope.Resolve<SquidDbContext>();
            var sessionId = Guid.NewGuid();

            db.Set<OctopusImportSession>().Add(Session(sessionId));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            scope.Resolve<IOctopusExportSecretsAccessor>().Secrets = OctopusExportSecrets.FromPassword(ExportPassword);

            var snapshot = await scope.Resolve<IOctopusImportPlanningPipeline>().BuildPreviewAsync(archivePath, SpaceId);
            snapshot.PreviewPlan.HasBlockers.ShouldBe(expectBlockers, customMessage: string.Join("; ", snapshot.PreviewPlan.Resources.SelectMany(r => r.Diagnostics).Concat(snapshot.PreviewPlan.Diagnostics).Where(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker).Select(d => $"{d.Code}:{d.SourceId}")));

            var result = await scope.Resolve<IOctopusImportConfirmationOrchestrator>().ConfirmAsync(new OctopusImportConfirmationRequest(sessionId, SpaceId, snapshot.Graph, snapshot.DependencyPlan, snapshot.PreviewPlan));

            result.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: string.Join("; ", (result.Result?.Diagnostics ?? []).Where(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker).Select(d => $"{d.Code}:{d.Message}")));
        });
    }

    private async Task<int> CreateReleaseAsync(string version)
    {
        var releaseId = 0;

        await Run<SquidDbContext, IReleaseService>(async (db, releaseService) =>
        {
            var project = await db.Set<Project>().SingleAsync(p => p.SpaceId == SpaceId && p.Name == "Compat.Api");
            var channel = await db.Set<Channel>().SingleAsync(c => c.ProjectId == project.Id && c.IsDefault);
            var release = await releaseService.CreateReleaseAsync(new CreateReleaseCommand { SpaceId = SpaceId, ProjectId = project.Id, ChannelId = channel.Id, Version = version });

            releaseId = release.Release.Id;
        });

        return releaseId;
    }

    private async Task<int> CreateUserAsync(string userName, string displayName)
    {
        var user = new UserAccount { UserName = userName, NormalizedUserName = userName.ToUpperInvariant(), DisplayName = displayName, PasswordHash = string.Empty, CreatedDate = DateTimeOffset.UtcNow, LastModifiedDate = DateTimeOffset.UtcNow };

        await Run<SquidDbContext>(async db =>
        {
            db.Set<UserAccount>().Add(user);
            await db.SaveChangesAsync();
        });

        return user.Id;
    }

    private async Task DeployAsync(int releaseId, string environmentName, int deployedBy)
    {
        await Run<SquidDbContext, IDeploymentService, IDeploymentTaskExecutor>(async (db, deploymentService, executor) =>
        {
            var environment = await db.Set<SquidEnvironment>().SingleAsync(e => e.SpaceId == SpaceId && e.Name == environmentName);
            var created = await deploymentService.CreateDeploymentAsync(new CreateDeploymentCommand { SpaceId = SpaceId, ReleaseId = releaseId, EnvironmentId = environment.Id, Name = $"Deploy to {environmentName}" });

            await db.Set<Deployment>().Where(d => d.Id == created.Deployment.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.DeployedBy, deployedBy).SetProperty(d => d.CreatedBy, deployedBy));

            await executor.ProcessAsync(created.TaskId, CancellationToken.None);

            db.ChangeTracker.Clear();
            (await db.Set<ServerTask>().AsNoTracking().SingleAsync(t => t.Id == created.TaskId)).State.ShouldBe(TaskState.Success, customMessage: $"deployment to {environmentName} must succeed");
        });
    }

    private static OctopusImportSession Session(Guid sessionId)
        => new()
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

    // ── Export ───────────────────────────────────────────────────────────────────────────────────

    private static List<(string Id, string DocumentType, string DocumentSource, string Json)> Documents(string scriptBody, string note, bool includeTemplate, Dictionary<string, string> extraActionProperties)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        string Id(string prefix) => $"{prefix}-{suffix}";

        var actionProperties = new Dictionary<string, string>
        {
            ["Octopus.Action.Script.ScriptSource"] = "Inline",
            ["Octopus.Action.Script.Syntax"] = "Bash",
            ["Octopus.Action.Script.ScriptBody"] = scriptBody,
            ["Octopus.Action.Template.Id"] = Id("ActionTemplates-9"),
            ["Octopus.Action.Template.Version"] = "3",
            ["Octopus.Action.RunOnServer"] = "true",
            ["BaseUrl"] = "https://hook.example",
            ["BotUrl"] = "#{BaseUrl}/send?release=#{Octopus.Release.Number}"
        };

        foreach (var (key, value) in extraActionProperties ?? []) actionProperties[key] = value;

        var documents = new List<(string Id, string DocumentType, string DocumentSource, string Json)>
        {
            Doc(Id("ProjectGroups-1"), "ProjectGroup", new { Id = Id("ProjectGroups-1"), Name = $"Compat {suffix}", Slug = $"compat-{suffix}" }),
            Doc(Id("Environments-2"), "StaticDeploymentEnvironment", new { Id = Id("Environments-2"), Name = "TEST", Slug = "test" }),
            Doc(Id("Environments-3"), "StaticDeploymentEnvironment", new { Id = Id("Environments-3"), Name = "PRD", Slug = "prd" }),
            Doc(Id("Lifecycles-1"), "Lifecycle", new { Id = Id("Lifecycles-1"), Name = $"Compat {suffix}", Slug = $"compat-lc-{suffix}", Phases = new[] { new { Id = Id("Phases-1"), Name = "All", AutomaticDeploymentTargets = new[] { Id("Environments-2") }, OptionalDeploymentTargets = new[] { Id("Environments-3") } } } }),
            Doc(Id("WorkerPools-1"), "StaticWorkerPool", new { Id = Id("WorkerPools-1"), Name = "Default Worker Pool" }),
            Doc(Id("LibraryVariableSets-1"), "LibraryVariableSet", new { Id = Id("LibraryVariableSets-1"), Name = $"Compat Lib {suffix}", Description = "", VariableSetId = Id("variableset-LibraryVariableSets-1"), ContentType = "Variables", Templates = Array.Empty<object>() }),
            Doc(Id("variableset-LibraryVariableSets-1"), "LibraryVariables", new
            {
                Id = Id("variableset-LibraryVariableSets-1"),
                OwnerId = Id("LibraryVariableSets-1"),
                OwnerType = "LibraryVariableSet",
                Variables = new object[] { new { Id = Guid.NewGuid().ToString(), Name = "Email:Imap:Password", Type = "Sensitive", Value = Encrypt(LibrarySecret, ExportPassword), Scope = new Dictionary<string, string[]> { ["Environment"] = [Id("Environments-2")] } } }
            }),
            Doc(Id("Projects-1"), "Project", new { Id = Id("Projects-1"), Name = "Compat.Api", Slug = $"compat-api-{suffix}", ProjectGroupId = Id("ProjectGroups-1"), LifecycleId = Id("Lifecycles-1"), VariableSetId = Id("variableset-Projects-1"), DeploymentProcessId = Id("deploymentprocess-Projects-1"), IncludedLibraryVariableSetIds = new[] { Id("LibraryVariableSets-1") } }),
            Doc(Id("Channels-1"), "Channel", new { Id = Id("Channels-1"), Name = "Default", ProjectId = Id("Projects-1"), LifecycleId = (string)null, IsDefault = true, Slug = "default" }),
            Doc(Id("variableset-Projects-1"), "ProjectVariables", new { Id = Id("variableset-Projects-1"), OwnerId = Id("Projects-1"), OwnerType = "Project", Variables = new object[] { new { Id = Guid.NewGuid().ToString(), Name = "App:Note", Value = note, Scope = new Dictionary<string, string[]>() } } }),
            Doc(Id("deploymentprocess-Projects-1"), "DeploymentProcess", new { Id = Id("deploymentprocess-Projects-1"), OwnerId = Id("Projects-1"), Steps = new[] { new { Id = Id("Steps-1"), Name = "Notify", Condition = "Success", StartTrigger = "StartAfterPrevious", PackageRequirement = "LetOctopusDecide", Properties = new Dictionary<string, string>(), Actions = new[] { new { Id = Id("Actions-1"), Name = "Notify", ActionType = "Octopus.Script", IsRequired = false, IsDisabled = false, Environments = Array.Empty<string>(), ExcludedEnvironments = Array.Empty<string>(), Channels = Array.Empty<string>(), Packages = Array.Empty<object>(), Properties = actionProperties } } } } })
        };

        if (includeTemplate)
            documents.Add(Doc(Id("ActionTemplates-9"), "ActionTemplate", new { Id = Id("ActionTemplates-9"), Name = $"Notify {suffix}", ActionType = "Octopus.Script", Version = 3, Properties = new Dictionary<string, string> { ["Octopus.Action.Script.Syntax"] = "Bash" }, Parameters = new[] { TemplateParameter("BaseUrl"), TemplateParameter("BotUrl") } }));

        return documents;
    }

    private static object TemplateParameter(string name) => new { Id = name, Name = name, Label = name, DefaultValue = "", DisplaySettings = new Dictionary<string, string> { ["Octopus.ControlType"] = "SingleLineText" } };

    private static (string Id, string DocumentType, string DocumentSource, string Json) Doc(string id, string documentType, object document)
        => (id, documentType, $"{id}.json", JsonSerializer.Serialize(document));

    private static string WriteArchive(IReadOnlyList<(string Id, string DocumentType, string DocumentSource, string Json)> documents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"squid-octopus-script-compat-{Guid.NewGuid():N}.zip");
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
