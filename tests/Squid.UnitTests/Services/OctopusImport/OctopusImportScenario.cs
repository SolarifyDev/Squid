using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>
/// Builds a realistic Octopus export (JSON documents + manifest) and runs it through the REAL
/// extraction → inventory → graph → dependency planner → conflict discovery → preview planner →
/// preview validator chain. Only the destination database is faked (a list of existing resources),
/// so preview/validation scenario tests exercise the same seams production does.
/// </summary>
internal sealed class OctopusImportScenario
{
    private readonly List<(string Id, string DocumentType, string Json)> _documents = [];
    private readonly List<OctopusImportDestinationResource> _destination = [];

    public OctopusImportScenario Environment(string id, string name)
        => Add(id, "StaticDeploymentEnvironment", new { Id = id, Name = name, Slug = name.ToLowerInvariant() });

    public OctopusImportScenario Machine(string id, string name, params string[] roles)
        => Add(id, "Machine", new { Id = id, Name = name, Roles = roles, EnvironmentIds = Array.Empty<string>() });

    public OctopusImportScenario Channel(string id, string projectId, string name)
        => Add(id, "Channel", new { Id = id, Name = name, ProjectId = projectId, IsDefault = false, Slug = name.ToLowerInvariant() });

    public OctopusImportScenario Team(string id, string name)
        => Add(id, "ConfigurableTeam", new { Id = id, Name = name, MemberUserIds = Array.Empty<string>(), ExternalSecurityGroups = Array.Empty<string>() });

    public OctopusImportScenario Library(string id, string name, params object[] variables)
        => LibraryDocument(id, name).LibraryVariables(id, variables);

    public OctopusImportScenario LibraryDocument(string id, string name)
        => Add(id, "LibraryVariableSet", new { Id = id, Name = name, VariableSetId = LibraryVariableSetId(id), ContentType = "Variables" });

    public OctopusImportScenario LibraryVariables(string libraryId, params object[] variables)
        => Add(LibraryVariableSetId(libraryId), "LibraryVariables", new { Id = LibraryVariableSetId(libraryId), OwnerId = libraryId, OwnerType = "LibraryVariableSet", Variables = variables });

    public OctopusImportScenario Project(string id, string name, string[] libraryIds, object[] variables, params object[] actions)
    {
        Add(id, "Project", new { Id = id, Name = name, Slug = name.ToLowerInvariant(), VariableSetId = ProjectVariableSetId(id), DeploymentProcessId = $"deploymentprocess-{id}", IncludedLibraryVariableSetIds = libraryIds });
        Add(ProjectVariableSetId(id), "ProjectVariables", new { Id = ProjectVariableSetId(id), OwnerId = id, OwnerType = "Project", Variables = variables });

        var steps = actions.Select((action, index) => new { Id = $"{id}-step-{index + 1}", Name = $"Step {index + 1}", Condition = "Success", StartTrigger = "StartAfterPrevious", PackageRequirement = "LetOctopusDecide", Properties = new Dictionary<string, string>(), Actions = new[] { action } });

        return Add($"deploymentprocess-{id}", "DeploymentProcess", new { Id = $"deploymentprocess-{id}", OwnerId = id, Steps = steps });
    }

    public OctopusImportScenario ExistingLibrary(int id, string name, DateTimeOffset? lastModified = null)
    {
        _destination.Add(new OctopusImportDestinationResource(id, SpaceId, OctopusResourceKind.LibraryVariableSet, name, null, lastModified ?? DateTimeOffset.UtcNow.AddDays(-1)));
        return this;
    }

    public OctopusImportScenario ExistingEnvironment(int id, string name)
    {
        _destination.Add(new OctopusImportDestinationResource(id, SpaceId, OctopusResourceKind.Environment, name, name.ToLowerInvariant(), DateTimeOffset.UtcNow.AddDays(-1)));
        return this;
    }

    public const int SpaceId = 7;

    public const string ExportPassword = "scenario-export-password";

    public static string LibraryVariableSetId(string libraryId) => $"variableset-{libraryId}";

    public static string ProjectVariableSetId(string projectId) => $"variableset-{projectId}";

    public static object Var(string id, string name, string value, Dictionary<string, string[]> scope = null)
        => new { Id = id, Name = name, Value = value, Type = "String", Scope = scope ?? new Dictionary<string, string[]>() };

    public static object Secret(string id, string name, string encryptedValue, Dictionary<string, string[]> scope = null)
        => new { Id = id, Name = name, Value = encryptedValue, Type = "Sensitive", IsSensitive = true, Scope = scope ?? new Dictionary<string, string[]>() };

    public static Dictionary<string, string[]> Scope(string key, params string[] values) => new() { [key] = values };

    public static object ScriptAction(string id, string scriptBody, Dictionary<string, string> extraProperties = null)
    {
        var properties = new Dictionary<string, string> { ["Octopus.Action.Script.ScriptSource"] = "Inline", ["Octopus.Action.Script.Syntax"] = "Bash", ["Octopus.Action.Script.ScriptBody"] = scriptBody, ["Octopus.Action.RunOnServer"] = "true" };

        foreach (var property in extraProperties ?? [])
            properties[property.Key] = property.Value;

        return new { Id = id, Name = id, ActionType = "Octopus.Script", Environments = Array.Empty<string>(), ExcludedEnvironments = Array.Empty<string>(), Channels = Array.Empty<string>(), Packages = Array.Empty<object>(), Properties = properties };
    }

    public static object ManualAction(string id, string responsibleTeamIds)
        => new { Id = id, Name = id, ActionType = "Octopus.Manual", Environments = Array.Empty<string>(), ExcludedEnvironments = Array.Empty<string>(), Channels = Array.Empty<string>(), Packages = Array.Empty<object>(), Properties = new Dictionary<string, string> { ["Octopus.Action.Manual.Instructions"] = "Approve", ["Octopus.Action.Manual.ResponsibleTeamIds"] = responsibleTeamIds, ["Octopus.Action.RunOnServer"] = "true" } };

    public static string Encrypt(string plaintext) => Secrets.OctopusExportTestCipher.Encrypt(plaintext, ExportPassword);

    public async Task<ScenarioResult> PlanAsync(OctopusExportSecrets secrets = null)
    {
        var graph = await BuildGraphAsync().ConfigureAwait(false);
        var dependencyPlan = new OctopusImportDependencyPlanner().BuildCurrentConfigurationPlan(graph);
        var conflicts = await DiscoverConflictsAsync(graph).ConfigureAwait(false);
        var accessor = new OctopusExportSecretsAccessor { Secrets = secrets ?? OctopusExportSecrets.None };
        var preview = new OctopusImportPreviewPlanner(accessor).BuildPreviewPlan(dependencyPlan, conflicts);
        var validation = new OctopusImportPreviewValidator(accessor).Validate(graph, dependencyPlan, conflicts, preview);

        return new ScenarioResult(graph, dependencyPlan, conflicts, preview, validation);
    }

    public async Task<OctopusResourceGraph> BuildGraphAsync()
    {
        var entries = new List<OctopusExtractedArchiveEntry> { new("manifest.json", Encoding.UTF8.GetBytes(BuildManifestJson())) };
        entries.AddRange(_documents.Select(d => new OctopusExtractedArchiveEntry($"{d.Id}.json", Encoding.UTF8.GetBytes(d.Json))));

        var extraction = await new OctopusInputExtractor().ExtractJsonEntriesAsync(entries).ConfigureAwait(false);

        return new OctopusResourceGraphBuilder().Build(new OctopusManifestInventoryBuilder().Build(extraction));
    }

    private async Task<OctopusImportConflictDiscoveryResult> DiscoverConflictsAsync(OctopusResourceGraph graph)
    {
        var destination = new Mock<IOctopusImportDestinationDataProvider>();
        destination.Setup(d => d.GetResourcesAsync(SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(_destination);

        return await new OctopusImportConflictDiscoveryService(destination.Object).DiscoverAsync(SpaceId, graph).ConfigureAwait(false);
    }

    private OctopusImportScenario Add(string id, string documentType, object document)
    {
        _documents.Add((id, documentType, JsonSerializer.Serialize(document)));
        return this;
    }

    private string BuildManifestJson()
        => JsonSerializer.Serialize(new
        {
            SchemaVersions = Array.Empty<string>(),
            Entries = _documents.Select(d => new { d.Id, Name = d.Id, d.DocumentType, ExportType = "FullDocument", DocumentSource = $"{d.Id}.json", ParentId = (string)null, Hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(d.Json))).ToLowerInvariant() })
        });

    internal sealed record ScenarioResult(OctopusResourceGraph Graph, OctopusImportDependencyPlan DependencyPlan, OctopusImportConflictDiscoveryResult Conflicts, OctopusImportPreviewPlanDto Preview, OctopusImportValidationResultDto Validation)
    {
        public IEnumerable<OctopusImportDiagnosticDto> AllDiagnostics => Preview.Diagnostics.Concat(Preview.Resources.SelectMany(r => r.Diagnostics)).Concat(Validation.Diagnostics);

        public IEnumerable<OctopusImportDiagnosticDto> Blockers => AllDiagnostics.Where(d => d.Severity == Squid.Message.Enums.OctopusImport.OctopusImportCompatibilitySeverity.Blocker);

        public bool BlocksConfirmation => Preview.HasBlockers || Validation.HasBlockers;

        public OctopusImportResourceResultDto Resource(string sourceId) => Preview.Resources.Single(r => r.SourceId == sourceId);
    }
}
