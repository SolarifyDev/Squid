using System.IO;
using System.Linq;
using Squid.Core.Services.DataSeeding;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Rest;

/// <summary>
/// Producer → consumer seam: the 2026.1-shaped REST fixtures go through the REAL normaliser and bundle writer, then the
/// REAL source reader → inventory → graph → dependency planner → preview planner → validator, exactly as a server-source
/// session will. Guards every reshaping the normaliser does by what the consumer actually reads.
/// </summary>
public sealed class OctopusRestBundleSeamTests : IDisposable
{
    private const int SpaceId = 7;

    private readonly string _sessionDirectory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"squid-rest-seam-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public async Task EveryFixture_ReadsBackWithoutAMalformedOrMisfiledDocument()
    {
        var (inventory, graph) = await BuildGraphAsync();

        inventory.Diagnostics.ShouldBeEmpty();
        graph.Diagnostics.ShouldNotContain(d => d.Code == OctopusInputExtractionDiagnosticCodes.GraphDocumentMalformed);
        graph.Diagnostics.Where(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker).ShouldBeEmpty();
        inventory.Items.ShouldAllBe(i => i.Classification.Kind == i.Document.Classification.Kind);
        graph.Resources.ShouldNotContain(r => r.SourceId == "feeds-builtin" || r.SourceId == "feeds-builtin-releases");
    }

    // REST has no OwnerType: without the injected one a library set is graphed as a project's set.
    [Theory]
    [InlineData("variableset-LibraryVariableSets-1", null, "LibraryVariableSets-1")]
    [InlineData("variableset-LibraryVariableSets-2", null, "LibraryVariableSets-2")]
    [InlineData("variableset-Projects-1", "Projects-1", null)]
    public async Task VariableSet_OwnedAsInOctopus(string setId, string ownerProjectId, string parentLibrary)
    {
        var (_, graph) = await BuildGraphAsync();

        var node = graph.Resources.Single(r => r.SourceId == setId);

        node.OwnerProjectId.ShouldBe(ownerProjectId);
        node.ParentSourceId.ShouldBe(parentLibrary);
    }

    // REST names the owner ProjectId: without the rename the steps hang off no project.
    [Theory]
    [InlineData("deploymentprocess-Projects-1", "Projects-1")]
    [InlineData("deploymentprocess-Projects-2", "Projects-2")]
    public async Task Process_ReferencesItsProject(string processId, string projectId)
    {
        var (_, graph) = await BuildGraphAsync();

        graph.References.ShouldContain(r => r.FromSourceId == processId && r.ReferenceKind == OctopusResourceReferenceKind.Project && r.ToSourceId == projectId);
        graph.Resources.Where(r => r.Kind == OctopusResourceKind.DeploymentStep && r.ParentSourceId == processId).ShouldAllBe(r => r.OwnerProjectId == projectId);
    }

    [Fact]
    public async Task SecretShapes_BindAsTheExportPipelineExpects()
    {
        var (_, graph) = await BuildGraphAsync();

        graph.Resources.Single(r => r.SourceId == "Feeds-1").GetSource<OctopusFeedDto>().Password.ShouldBeNull();
        graph.Resources.Single(r => r.SourceId == "Accounts-2").GetSource<OctopusAccountDto>().Credentials!.Value.GetProperty("Token").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        graph.Resources.Single(r => r.SourceId == "Teams-2").GetSource<OctopusTeamDto>().ExternalSecurityGroups.ShouldBe(["S-1-5-21-0000000000-0000000000-0000000000-1001"]);
        graph.Resources.Single(r => r.SourceId == "deploymentprocess-Projects-2").GetSource<OctopusDeploymentProcessDto>().Steps[0].Actions[0].ExtensionData[OctopusRestDocumentNormalizer.WithheldSecretsProperty].EnumerateArray().Select(e => e.GetString()).ShouldBe(["Octopus.Action.WindowsService.CustomAccountPassword"]);
        graph.Resources.Single(r => r.SourceId == "Projects-1").GetSource<OctopusProjectDto>().DeploymentSettingsId.ShouldBe("deploymentsettings-Projects-1");
        graph.Resources.ShouldContain(r => r.SourceId == "deploymentsettings-Projects-1" && r.Kind == OctopusResourceKind.DeploymentSettings);
    }

    [Fact]
    public async Task PromptedSelectList_BindsAndIsRecognisedAsHavingDisplaySettings()
    {
        var (_, graph) = await BuildGraphAsync();

        var variable = graph.Resources.Single(r => r.Kind == OctopusResourceKind.Variable && r.Name == "Worker.Mode").GetSource<OctopusVariableDto>();

        variable.Prompt.HasDisplaySettings.ShouldBeTrue();
    }

    // Planning the whole fixture space must not trip over the bundle; the only blockers left are the withheld secrets
    // and history/scope rules that later work packages own (recorded here so a new blocker cannot slip in silently).
    [Fact]
    public async Task WholeFixtureSpace_Plans()
    {
        var (_, graph) = await BuildGraphAsync();
        var dependencyPlan = new OctopusImportDependencyPlanner().BuildCurrentConfigurationPlan(graph);
        var conflicts = await new OctopusImportConflictDiscoveryService(EmptyDestination(), EmptySourceMap()).DiscoverAsync(SpaceId, graph);
        var accessor = new OctopusExportSecretsAccessor { Secrets = OctopusExportSecrets.None };

        var preview = new OctopusImportPreviewPlanner(accessor).BuildPreviewPlan(dependencyPlan, conflicts);
        var validation = new OctopusImportPreviewValidator(accessor).Validate(graph, dependencyPlan, conflicts, preview);

        var blockers = preview.Diagnostics.Concat(preview.Resources.SelectMany(r => r.Diagnostics)).Concat(validation.Diagnostics).Where(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker).Select(d => d.Code).Distinct().Order().ToList();

        string.Join(", ", blockers).ShouldBe(string.Join(", ", ExpectedBlockersOwnedByLaterWork));
    }

    // Withheld sensitive variable values: still a Blocker until the severity SSOT makes server-withheld secrets a Warning.
    private static readonly string[] ExpectedBlockersOwnedByLaterWork = [OctopusImportPreviewDiagnosticCodes.RequiredSensitiveVariableInputMissing];

    public void Dispose() => Directory.Delete(_sessionDirectory, recursive: true);

    private async Task<(OctopusManifestInventoryResult Inventory, OctopusResourceGraph Graph)> BuildGraphAsync()
    {
        var writer = new OctopusHistoryBundleWriter(new OctopusImportSizeLimits());

        foreach (var (fixture, kind) in OctopusRestFixtureKinds.All)
        foreach (var resource in OctopusServer2026Fixtures.Resources(fixture))
        {
            var normalized = OctopusRestDocumentNormalizer.Normalize(kind, resource, OctopusRestFixtureKinds.OwnerOf(fixture));

            if (normalized != null) await writer.WriteJsonAsync(_sessionDirectory, kind, normalized.Document);
        }

        var bundle = await writer.BuildBundleAsync(_sessionDirectory, "inst-1");
        var extraction = await new OctopusImportSourceReader(new OctopusArchiveExtractor(), new OctopusInputExtractor(), new OctopusImportSizeLimits()).ReadAsync(bundle);
        var inventory = new OctopusManifestInventoryBuilder().Build(extraction);

        return (inventory, new OctopusResourceGraphBuilder().Build(inventory));
    }

    private static IOctopusImportDestinationDataProvider EmptyDestination()
    {
        var destination = new Mock<IOctopusImportDestinationDataProvider>();
        destination.Setup(d => d.GetResourcesAsync(SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync([SquidBuiltInTeam(1, BuiltInTeamNames.Everyone), SquidBuiltInTeam(2, BuiltInTeamNames.Administrators)]);
        destination.Setup(d => d.GetReleaseKeysAsync(SpaceId, It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return destination.Object;
    }

    // Every Squid space is seeded with these; the import maps Octopus's built-in teams onto them.
    private static Squid.Core.Services.OctopusImport.OctopusImportDestinationResource SquidBuiltInTeam(int id, string name) => new(id, 0, OctopusResourceKind.Team, name, null, DateTimeOffset.UtcNow.AddDays(-1)) { IsBuiltIn = true, MemberCount = 0 };

    private static IOctopusImportSourceMapDataProvider EmptySourceMap()
    {
        var sourceMap = new Mock<IOctopusImportSourceMapDataProvider>();
        sourceMap.Setup(s => s.LoadAsync(SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(OctopusImportPriorMappings.Empty);
        return sourceMap.Object;
    }
}
