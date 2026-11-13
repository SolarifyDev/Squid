using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportPriorMappingPolicyTests
{
    public enum PriorScenario
    {
        NoHit,
        HitSame,
        HitChanged,
        OwnerAlreadyImported
    }

    private const int PriorDestinationId = 500;
    private const string OwnerId = "Owner-1";

    // Pinned independently of production: a kind moving between these sets is a deliberate policy change.
    private static readonly HashSet<OctopusResourceKind> ConfigurationKindsWithOwnRows =
    [
        OctopusResourceKind.ProjectGroup, OctopusResourceKind.Environment, OctopusResourceKind.Lifecycle, OctopusResourceKind.LifecyclePhase,
        OctopusResourceKind.Feed, OctopusResourceKind.Account, OctopusResourceKind.Team, OctopusResourceKind.LibraryVariableSet,
        OctopusResourceKind.Project, OctopusResourceKind.Channel, OctopusResourceKind.VariableSet, OctopusResourceKind.Variable,
        OctopusResourceKind.DeploymentProcess, OctopusResourceKind.DeploymentStep, OctopusResourceKind.DeploymentAction
    ];

    private static readonly HashSet<OctopusResourceKind> KindsInheritingFromOwner =
    [
        OctopusResourceKind.LifecyclePhase, OctopusResourceKind.Channel, OctopusResourceKind.DeploymentSettings, OctopusResourceKind.DeploymentProcess,
        OctopusResourceKind.DeploymentStep, OctopusResourceKind.DeploymentAction, OctopusResourceKind.VariableSet, OctopusResourceKind.Variable
    ];

    public static IEnumerable<object[]> EveryKindAndScenario()
        => from kind in Enum.GetValues<OctopusResourceKind>() from scenario in Enum.GetValues<PriorScenario>() select new object[] { kind, scenario };

    [Theory]
    [MemberData(nameof(EveryKindAndScenario))]
    public void Decide_EveryKind_Theory(OctopusResourceKind kind, PriorScenario scenario)
    {
        var node = NodeFor(kind);

        var decision = OctopusImportPriorMappingPolicy.Decide(node, PriorFor(node, scenario), OwnerActionFor(scenario));

        var expected = Expected(kind, scenario);
        if (expected == null)
        {
            decision.ShouldBeNull($"{kind} / {scenario} must keep the existing (non-prior) behaviour");
            return;
        }

        decision.ShouldNotBeNull($"{kind} / {scenario}");
        decision.Action.ShouldBe(expected.Action, $"{kind} / {scenario}");
        decision.DestinationId.ShouldBe(expected.DestinationId, $"{kind} / {scenario}");
        decision.Diagnostic?.Code.ShouldBe(expected.Diagnostic?.Code, $"{kind} / {scenario}");
        (decision.Diagnostic == null).ShouldBe(expected.Diagnostic == null, $"{kind} / {scenario}");
    }

    [Fact]
    public void DefaultChannelUnderExistingProject_IsCreatedAsARegularChannel()
    {
        var channel = Node(OctopusResourceKind.Channel, "Channels-1", parentSourceId: null, ownerProjectId: "Projects-1", source: new OctopusChannelDto { Id = "Channels-1", Name = "Default", IsDefault = true });

        var decision = OctopusImportPriorMappingPolicy.Decide(channel, OctopusImportPriorMappings.Empty, id => id == "Projects-1" ? OctopusImportPreviewAction.AlreadyImported : null);

        decision.Action.ShouldBe(OctopusImportPreviewAction.Create, "skipping it would skip every release and deployment on Octopus' new default channel");
        decision.DestinationId.ShouldBeNull();
        decision.Diagnostic.Severity.ShouldBe(OctopusImportCompatibilitySeverity.Info);
        decision.Diagnostic.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.DefaultChannelNotRemapped);
        decision.Diagnostic.SourceId.ShouldBe("Channels-1");
    }

    [Fact]
    public void NonDefaultChannelUnderNewProject_KeepsExistingBehaviour()
    {
        var channel = Node(OctopusResourceKind.Channel, "Channels-1", parentSourceId: null, ownerProjectId: "Projects-1", source: new OctopusChannelDto { Id = "Channels-1", IsDefault = true });

        OctopusImportPriorMappingPolicy.Decide(channel, OctopusImportPriorMappings.Empty, _ => OctopusImportPreviewAction.Create).ShouldBeNull();
        OctopusImportPriorMappingPolicy.Decide(channel, OctopusImportPriorMappings.Empty, null).ShouldBeNull();
    }

    [Fact]
    public void PriorMappedDefaultChannel_StaysAlreadyImported()
    {
        var channel = Node(OctopusResourceKind.Channel, "Channels-1", parentSourceId: null, ownerProjectId: "Projects-1", source: new OctopusChannelDto { Id = "Channels-1", IsDefault = true });
        var prior = Prior(("Channel", "channels-1", new OctopusImportPriorMapping(42, "Created", null, null)));

        var decision = OctopusImportPriorMappingPolicy.Decide(channel, prior, _ => OctopusImportPreviewAction.AlreadyImported);

        decision.Action.ShouldBe(OctopusImportPreviewAction.AlreadyImported);
        decision.DestinationId.ShouldBe(42);
    }

    [Theory]
    [InlineData(OctopusResourceKind.LifecyclePhase, "Lifecycles-1", null, "Lifecycles-1")]
    [InlineData(OctopusResourceKind.Channel, null, "Projects-1", "Projects-1")]
    [InlineData(OctopusResourceKind.VariableSet, "LibraryVariableSets-1", null, "LibraryVariableSets-1")]
    [InlineData(OctopusResourceKind.VariableSet, null, "Projects-1", "Projects-1")]
    [InlineData(OctopusResourceKind.Variable, "variableset-Projects-1", "Projects-1", "variableset-Projects-1")]
    [InlineData(OctopusResourceKind.DeploymentAction, "Steps-1", "Projects-1", "Steps-1")]
    [InlineData(OctopusResourceKind.Project, null, "Projects-1", null)]
    [InlineData(OctopusResourceKind.Release, null, "Projects-1", null)]
    public void OwnerSourceId_IsTheStructuralParentElseTheProject(OctopusResourceKind kind, string parentSourceId, string ownerProjectId, string expectedOwner)
    {
        OctopusImportPriorMappingPolicy.OwnerSourceId(Node(kind, "X-1", parentSourceId, ownerProjectId, new object())).ShouldBe(expectedOwner);
    }

    [Theory]
    [InlineData(null, "dv-1", false)]
    [InlineData("dv-0", null, false)]
    [InlineData("dv-1", "dv-1", false)]
    [InlineData("dv-0", "dv-1", true)]
    public void ChangedSinceLastImport_OnlyWhenBothFingerprintsAreKnownAndDiffer(string storedDataVersion, string incomingDataVersion, bool expectInfo)
    {
        var node = Node(OctopusResourceKind.Environment, "Environments-1", null, null, new OctopusEnvironmentDto { Id = "Environments-1", DataVersion = incomingDataVersion });
        var stored = storedDataVersion == null ? null : OctopusImportFingerprint.ForConfig(new OctopusEnvironmentDto { DataVersion = storedDataVersion });
        var prior = Prior(("Environment", "Environments-1", new OctopusImportPriorMapping(7, "Created", stored, null)));

        var decision = OctopusImportPriorMappingPolicy.Decide(node, prior, null);

        decision.Action.ShouldBe(OctopusImportPreviewAction.AlreadyImported);
        (decision.Diagnostic?.Severity == OctopusImportCompatibilitySeverity.Info).ShouldBe(expectInfo);
    }

    [Fact]
    public void Release_NullStoredFingerprint_Update()
    {
        var release = Node(OctopusResourceKind.Release, "Releases-1", null, "Projects-1", new OctopusReleaseDto { Id = "Releases-1", ReleaseNotes = "notes" });
        var prior = Prior(("Release", "Releases-1", new OctopusImportPriorMapping(9, "Created", null, null)));

        var decision = OctopusImportPriorMappingPolicy.Decide(release, prior, null);

        decision.Action.ShouldBe(OctopusImportPreviewAction.Update);
        decision.DestinationId.ShouldBe(9);
    }

    [Theory]
    [InlineData(false, null, null, null, OctopusImportPreviewAction.Create)]
    [InlineData(true, "same", null, null, OctopusImportPreviewAction.AlreadyImported)]
    [InlineData(true, "same", "d1", null, OctopusImportPreviewAction.AlreadyImported)]
    [InlineData(true, "same", "d1", "d1", OctopusImportPreviewAction.AlreadyImported)]
    [InlineData(true, "same", "d1", "d2", OctopusImportPreviewAction.Update)]
    [InlineData(true, "same", null, "d1", OctopusImportPreviewAction.Update)]
    [InlineData(true, "other", "d1", null, OctopusImportPreviewAction.Update)]
    [InlineData(true, null, null, null, OctopusImportPreviewAction.Update)]
    public void DecideHistoryUnit_Theory(bool hasRow, string storedTask, string storedDetails, string incomingDetails, OctopusImportPreviewAction expected)
    {
        var task = new OctopusServerTaskDto { Id = "ServerTasks-1", State = "Success", DetailFingerprint = incomingDetails };
        var storedFingerprint = storedTask switch { "same" => OctopusImportFingerprint.ForTask(task), "other" => OctopusImportFingerprint.ForTask(new OctopusServerTaskDto { State = "Executing" }), _ => null };
        var stored = hasRow ? new OctopusImportPriorMapping(11, "Created", storedFingerprint, storedDetails) : null;

        OctopusImportPriorMappingPolicy.DecideHistoryUnit(stored, task).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null, "INSTALL-A", false)]
    [InlineData("install-a", null, false)]
    [InlineData(" install-a ", "INSTALL-A", false)]
    [InlineData("install-b", "INSTALL-A", true)]
    public void InstallationMismatch_BlocksOnlyAKnownDifferentInstallation(string incoming, string stored, bool expectBlocker)
    {
        var prior = new OctopusImportPriorMappings(new Dictionary<(string Type, string Id), OctopusImportPriorMapping>(), stored);

        var diagnostic = OctopusImportPriorMappingPolicy.InstallationMismatch(incoming, prior);

        (diagnostic != null).ShouldBe(expectBlocker);
        diagnostic?.Severity.ShouldBe(OctopusImportCompatibilitySeverity.Blocker);
        diagnostic?.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.OctopusInstallationMismatch);
    }

    private static OctopusImportPriorDecision Expected(OctopusResourceKind kind, PriorScenario scenario)
    {
        if (kind == OctopusResourceKind.Release)
            return scenario switch
            {
                PriorScenario.HitSame => new(OctopusImportPreviewAction.AlreadyImported, PriorDestinationId),
                PriorScenario.HitChanged => new(OctopusImportPreviewAction.Update, PriorDestinationId),
                _ => null
            };

        if (scenario is PriorScenario.HitSame or PriorScenario.HitChanged)
            return ConfigurationKindsWithOwnRows.Contains(kind) ? new(OctopusImportPreviewAction.AlreadyImported, PriorDestinationId, scenario == PriorScenario.HitChanged ? ChangedInfo() : null) : null;

        if (scenario == PriorScenario.OwnerAlreadyImported && KindsInheritingFromOwner.Contains(kind))
            return kind == OctopusResourceKind.Channel ? new(OctopusImportPreviewAction.Create, null) : new(OctopusImportPreviewAction.AlreadyImported, null);

        return null;
    }

    private static OctopusImportDiagnosticDto ChangedInfo() => new() { Code = OctopusImportPreviewDiagnosticCodes.ChangedSinceLastImport };

    private static OctopusImportPriorMappings PriorFor(OctopusResourceNode node, PriorScenario scenario)
    {
        if (scenario is not (PriorScenario.HitSame or PriorScenario.HitChanged)) return OctopusImportPriorMappings.Empty;

        var changed = scenario == PriorScenario.HitChanged;
        var fingerprint = node.Kind == OctopusResourceKind.Release
            ? OctopusImportFingerprint.ForRelease(new OctopusReleaseDto { ReleaseNotes = changed ? "old notes" : "notes" })
            : OctopusImportFingerprint.ForConfig(new TestDocument { DataVersion = changed ? "dv-0" : "dv-1" });

        return Prior((node.Kind.ToString(), node.SourceId, new OctopusImportPriorMapping(PriorDestinationId, "Created", fingerprint, null)));
    }

    private static Func<string, OctopusImportPreviewAction?> OwnerActionFor(PriorScenario scenario)
        => ownerId => ownerId == OwnerId ? scenario == PriorScenario.OwnerAlreadyImported ? OctopusImportPreviewAction.AlreadyImported : OctopusImportPreviewAction.Create : null;

    private static OctopusResourceNode NodeFor(OctopusResourceKind kind)
    {
        object source = kind switch
        {
            OctopusResourceKind.Release => new OctopusReleaseDto { Id = $"{kind}-1", ReleaseNotes = "notes" },
            OctopusResourceKind.Channel => new OctopusChannelDto { Id = $"{kind}-1", IsDefault = false, DataVersion = "dv-1" },
            _ => new TestDocument { Id = $"{kind}-1", DataVersion = "dv-1" }
        };

        return Node(kind, $"{kind}-1", OwnerId, "Projects-9", source);
    }

    private static OctopusResourceNode Node(OctopusResourceKind kind, string sourceId, string parentSourceId, string ownerProjectId, object source)
        => new(sourceId, sourceId, kind, OctopusDocumentKind.Project, $"{sourceId}.json", ownerProjectId, parentSourceId, false, source);

    private static OctopusImportPriorMappings Prior(params (string Type, string Id, OctopusImportPriorMapping Mapping)[] rows)
        => new(rows.ToDictionary(r => (r.Type, r.Id), r => r.Mapping), null);

    private sealed class TestDocument : OctopusDocumentDto;
}
