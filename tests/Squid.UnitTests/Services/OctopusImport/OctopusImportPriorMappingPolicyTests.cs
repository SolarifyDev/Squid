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
        decision.Choice.ShouldBe(expected.Choice, $"{kind} / {scenario}");
    }

    // A child without a live row under an imported owner is the user's to decide, whichever kind: the source map tells the reason, and
    // only a channel added in Octopus is selected by default (nothing changes silently; what the user deleted stays deleted).
    [Theory]
    [InlineData(OctopusResourceKind.DeploymentStep, true, OctopusImportChoiceReason.DeletedInSquid, false)]
    [InlineData(OctopusResourceKind.DeploymentStep, false, OctopusImportChoiceReason.AddedInOctopus, false)]
    [InlineData(OctopusResourceKind.DeploymentAction, true, OctopusImportChoiceReason.DeletedInSquid, false)]
    [InlineData(OctopusResourceKind.DeploymentAction, false, OctopusImportChoiceReason.AddedInOctopus, false)]
    [InlineData(OctopusResourceKind.LifecyclePhase, true, OctopusImportChoiceReason.DeletedInSquid, false)]
    [InlineData(OctopusResourceKind.LifecyclePhase, false, OctopusImportChoiceReason.AddedInOctopus, false)]
    [InlineData(OctopusResourceKind.Variable, true, OctopusImportChoiceReason.DeletedInSquid, false)]
    [InlineData(OctopusResourceKind.Variable, false, OctopusImportChoiceReason.AddedInOctopus, false)]
    [InlineData(OctopusResourceKind.Channel, true, OctopusImportChoiceReason.DeletedInSquid, false)]
    [InlineData(OctopusResourceKind.Channel, false, OctopusImportChoiceReason.AddedInOctopus, true)]
    public void ChildWithoutLiveRowUnderImportedOwner_IsAChoice_ReasonFromTheSourceMap_DefaultSelectedOnlyForAnAddedChannel(OctopusResourceKind kind, bool recordedBefore, OctopusImportChoiceReason expectedReason, bool expectedDefault)
    {
        var node = NodeFor(kind);
        var prior = new OctopusImportPriorMappings(new Dictionary<(string Type, string Id), OctopusImportPriorMapping>(), null, deletedKeys: recordedBefore ? new HashSet<(string Type, string Id)> { (kind.ToString(), node.SourceId) } : null);

        var decision = OctopusImportPriorMappingPolicy.Decide(node, prior, OwnerActionFor(PriorScenario.OwnerAlreadyImported));

        decision.Action.ShouldBe(OctopusImportPreviewAction.Create, "Create is what a selected child does");
        decision.Choice.ShouldBe(new OctopusImportChoice(expectedReason, expectedDefault));
    }

    // A child deleted in Squid before owner-qualified ids left a bare-id row the migration could not tie to an owner (its Squid row is
    // gone): it still names the child as deleted, unless two children of the source share that bare id (then it names neither).
    [Theory]
    [InlineData(OctopusResourceKind.DeploymentStep, "Projects-1/step/a1", "A1", false, OctopusImportChoiceReason.DeletedInSquid)]
    [InlineData(OctopusResourceKind.DeploymentAction, "Projects-1/step/a1/action-2", "A1/ACTION-2", false, OctopusImportChoiceReason.DeletedInSquid)]
    [InlineData(OctopusResourceKind.Variable, "Projects-1/variable/c3", "c3", false, OctopusImportChoiceReason.DeletedInSquid)]
    [InlineData(OctopusResourceKind.LifecyclePhase, "Lifecycles-1/phase/d4", "d4", false, OctopusImportChoiceReason.DeletedInSquid)]
    [InlineData(OctopusResourceKind.DeploymentStep, "Projects-1/step/a1", "A1", true, OctopusImportChoiceReason.AddedInOctopus)]
    [InlineData(OctopusResourceKind.DeploymentStep, "Projects-1/step/a1", "B9", false, OctopusImportChoiceReason.AddedInOctopus)]
    [InlineData(OctopusResourceKind.DeploymentStep, "deploymentprocess-Projects-1/step-1", "STEP-1", false, OctopusImportChoiceReason.AddedInOctopus)]
    [InlineData(OctopusResourceKind.Variable, "Projects-1/variable/a1", "A1", false, OctopusImportChoiceReason.AddedInOctopus)]   // a bare row of another kind
    public void ChildDeletedBeforeOwnerQualifiedIds_BareRow_StillNamesItDeleted(OctopusResourceKind kind, string sourceId, string bareRecordedId, bool sharedInSource, OctopusImportChoiceReason expectedReason)
    {
        var node = Node(kind, sourceId, OwnerId, "Projects-1", new TestDocument { Id = sourceId });
        var recordedKind = expectedReason == OctopusImportChoiceReason.AddedInOctopus && kind == OctopusResourceKind.Variable ? OctopusResourceKind.DeploymentStep : kind;
        var prior = new OctopusImportPriorMappings(new Dictionary<(string Type, string Id), OctopusImportPriorMapping>(), null, deletedKeys: new HashSet<(string Type, string Id)> { (recordedKind.ToString(), bareRecordedId) });
        var twin = Node(kind, sourceId.Replace("Projects-1", "Projects-2").Replace("Lifecycles-1", "Lifecycles-2"), OwnerId, "Projects-2", new TestDocument());
        var shared = OctopusImportPriorMappingPolicy.SharedUnqualifiedIds(sharedInSource ? [node, twin] : [node]);

        var decision = OctopusImportPriorMappingPolicy.Decide(node, prior, OwnerActionFor(PriorScenario.OwnerAlreadyImported), new OctopusImportLegacyChildIds(shared, OctopusImportPriorMappingPolicy.RecordedUnqualifiedIds(prior)));

        decision.Choice.ShouldBe(new OctopusImportChoice(expectedReason, false));
    }

    // An import made before owner-qualified ids linked a clone's children to the original's rows (they share raw ids), so the clone's
    // children never reached Squid; the migration then qualified those rows by the original. A child with no live row of its own whose bare
    // id is live under another owner can only come from that: it was missed, and is selected (its owner was imported meaning all of it).
    [Theory]
    [InlineData(OctopusResourceKind.DeploymentStep, "Projects-2/step/a1", "live", "Projects-1/STEP/A1", null, OctopusImportChoiceReason.MissedByEarlierImport)]
    [InlineData(OctopusResourceKind.DeploymentAction, "Projects-2/action/b2", "live", "Projects-1/ACTION/B2", null, OctopusImportChoiceReason.MissedByEarlierImport)]
    [InlineData(OctopusResourceKind.DeploymentAction, "Projects-2/step/a1/action-1", "live", "Projects-1/STEP/A1/ACTION-1", null, OctopusImportChoiceReason.MissedByEarlierImport)]
    [InlineData(OctopusResourceKind.Variable, "Projects-2/variable/c3", "live", "Projects-1/VARIABLE/C3", null, OctopusImportChoiceReason.MissedByEarlierImport)]
    [InlineData(OctopusResourceKind.LifecyclePhase, "Lifecycles-2/phase/d4", "live", "Lifecycles-1/PHASE/D4", null, OctopusImportChoiceReason.MissedByEarlierImport)]
    [InlineData(OctopusResourceKind.DeploymentStep, "Projects-2/step/a1", "live", "Projects-1/STEP/A1", "Projects-2/step/a1", OctopusImportChoiceReason.DeletedInSquid)]   // its own deleted row wins
    [InlineData(OctopusResourceKind.DeploymentStep, "Projects-2/step/a1", "dead", "Projects-1/STEP/A1", null, OctopusImportChoiceReason.AddedInOctopus)]                   // the other owner's row is gone
    [InlineData(OctopusResourceKind.DeploymentStep, "Projects-2/step/a1", "live", "A1", null, OctopusImportChoiceReason.AddedInOctopus)]                                   // a raw row names no owner
    [InlineData(OctopusResourceKind.DeploymentStep, "Projects-2/step/a1", "live", "Projects-1/STEP/B9", null, OctopusImportChoiceReason.AddedInOctopus)]                   // another bare id
    [InlineData(OctopusResourceKind.Variable, "Projects-2/variable/a1", "live", "Projects-1/STEP/A1", null, OctopusImportChoiceReason.AddedInOctopus)]                     // a row of another kind
    [InlineData(OctopusResourceKind.DeploymentStep, "Projects-2/step/a1", "none", null, null, OctopusImportChoiceReason.AddedInOctopus)]                                   // no earlier import
    public void ChildMissedByLegacyImport_BareIdLiveUnderAnotherOwner_IsMissedAndSelected(OctopusResourceKind kind, string sourceId, string recorded, string recordedId, string deletedOwnId, OctopusImportChoiceReason expectedReason)
    {
        var node = Node(kind, sourceId, OwnerId, "Projects-2", new TestDocument { Id = sourceId });
        var recordedKind = kind == OctopusResourceKind.Variable && recordedId?.Contains("/STEP/") == true ? OctopusResourceKind.DeploymentStep : kind;
        var live = recorded == "live" ? new Dictionary<(string Type, string Id), OctopusImportPriorMapping> { [(recordedKind.ToString(), recordedId)] = new OctopusImportPriorMapping(PriorDestinationId, "Created", null, null) } : new();
        var deleted = new HashSet<(string Type, string Id)>();
        if (recorded == "dead") deleted.Add((recordedKind.ToString(), recordedId));
        if (deletedOwnId != null) deleted.Add((kind.ToString(), deletedOwnId));
        var prior = new OctopusImportPriorMappings(live, null, deletedKeys: deleted);

        var decision = OctopusImportPriorMappingPolicy.Decide(node, prior, OwnerActionFor(PriorScenario.OwnerAlreadyImported), new OctopusImportLegacyChildIds(new HashSet<(string Type, string Id)>(), OctopusImportPriorMappingPolicy.RecordedUnqualifiedIds(prior)));

        decision.Action.ShouldBe(OctopusImportPreviewAction.Create, "Create is what a selected child does");
        decision.Choice.ShouldBe(new OctopusImportChoice(expectedReason, expectedReason == OctopusImportChoiceReason.MissedByEarlierImport));
    }

    [Fact]
    public void RecordedUnqualifiedIds_OnlyLiveOwnerQualifiedRows()
    {
        var prior = new OctopusImportPriorMappings(new Dictionary<(string Type, string Id), OctopusImportPriorMapping>
        {
            [("DeploymentStep", "Projects-1/STEP/A1")] = new(1, "Created", null, null),
            [("DeploymentStep", "B2")] = new(2, "Created", null, null),
            [("Project", "Projects-1")] = new(3, "Created", null, null)
        }, null, deletedKeys: new HashSet<(string Type, string Id)> { ("DeploymentStep", "Projects-1/STEP/C3") });

        OctopusImportPriorMappingPolicy.RecordedUnqualifiedIds(prior).ShouldBe([("DEPLOYMENTSTEP", "A1")], ignoreOrder: true);
    }

    public static IEnumerable<object[]> EveryChoosableKindAndReason()
        => from kind in Enum.GetValues<OctopusResourceKind>().Where(OctopusImportPriorMappingPolicy.IsChoosable) from reason in Enum.GetValues<OctopusImportChoiceReason>() select new object[] { kind, reason };

    // Nothing changes silently: a channel added in Octopus is selected (its history needs it), and so is a child an earlier import missed
    // (its owner was imported meaning all of it); what the user deleted stays deleted and anything else added in Octopus waits for a tick.
    [Theory]
    [MemberData(nameof(EveryChoosableKindAndReason))]
    public void DefaultSelected_EveryChoosableKindAndReason(OctopusResourceKind kind, OctopusImportChoiceReason reason)
    {
        var expected = reason == OctopusImportChoiceReason.MissedByEarlierImport || (reason == OctopusImportChoiceReason.AddedInOctopus && kind == OctopusResourceKind.Channel);

        OctopusImportPriorMappingPolicy.DefaultSelected(kind, reason).ShouldBe(expected, $"{kind} / {reason}");
    }

    [Fact]
    public void DefaultChannelUnderImportedProject_IsStillAChoice_AndKeepsItsNotRemappedNote()
    {
        var channel = Node(OctopusResourceKind.Channel, "Channels-1", parentSourceId: null, ownerProjectId: "Projects-1", source: new OctopusChannelDto { Id = "Channels-1", Name = "Default", IsDefault = true });

        var decision = OctopusImportPriorMappingPolicy.Decide(channel, OctopusImportPriorMappings.Empty, id => id == "Projects-1" ? OctopusImportPreviewAction.AlreadyImported : null);

        decision.Choice.ShouldBe(new OctopusImportChoice(OctopusImportChoiceReason.AddedInOctopus, true));
        decision.Diagnostic.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.DefaultChannelNotRemapped);
    }

    // The literal codes are a wire contract with SquidWeb, which words and groups the choices by them.
    [Fact]
    public void ChoiceDiagnosticCodes_ArePinned()
    {
        OctopusImportPreviewDiagnosticCodes.ChildDeletedInSquid.ShouldBe("octopus.preview.child_deleted_in_squid");
        OctopusImportPreviewDiagnosticCodes.ChildAddedInOctopus.ShouldBe("octopus.preview.child_added_in_octopus");
        OctopusImportPreviewDiagnosticCodes.HistoryOwnerNotSelected.ShouldBe("octopus.preview.history_owner_not_selected");
        OctopusImportPreviewDiagnosticCodes.ChildMissedByEarlierImport.ShouldBe("octopus.preview.child_missed_by_earlier_import");
        ((int)OctopusImportChoiceReason.DeletedInSquid, (int)OctopusImportChoiceReason.AddedInOctopus, (int)OctopusImportChoiceReason.MissedByEarlierImport).ShouldBe((0, 1, 2), "the SquidWeb mirror of the reason is ordinal");
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

    // A release update may rewrite Squid's notes only while they are what the last import wrote: an edit (or a clear) made in Squid is the user's.
    [Theory]
    [InlineData("imported", "imported", false)]
    [InlineData("imported", "  imported  ", false)]                    // canonical: surrounding whitespace is not an edit
    [InlineData("", "", false)]                                        // imported empty, still empty
    [InlineData("", null, false)]
    [InlineData("imported", "imported + approved by ops", true)]
    [InlineData("imported", "", true)]                                 // cleared in Squid
    [InlineData(null, "", false)]                                      // no fingerprint recorded (older import): only empty notes are not the user's
    [InlineData(null, "written in Squid", true)]
    public void ReleaseNotesEditedInSquid_OnlyWhenSquidNoLongerHoldsWhatTheImportWrote(string importedNotes, string squidNotes, bool expected)
    {
        var fingerprint = importedNotes == null ? null : OctopusImportFingerprint.ForRelease(new OctopusReleaseDto { ReleaseNotes = importedNotes });

        OctopusImportPriorMappingPolicy.ReleaseNotesEditedInSquid(new OctopusImportPriorMapping(9, "Created", fingerprint, null), squidNotes).ShouldBe(expected);
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

        // A child the earlier import would have recorded but did not was added in Octopus since: the user decides (Create is what a
        // selected child does; only a channel is selected by default). The project's deployment settings have no rows of their own and are
        // what the owner's import wrote; a process or variable set without a row is never written into the imported project again.
        if (scenario == PriorScenario.OwnerAlreadyImported && KindsInheritingFromOwner.Contains(kind))
            return kind switch
            {
                OctopusResourceKind.Channel => new(OctopusImportPreviewAction.Create, null, Choice: new OctopusImportChoice(OctopusImportChoiceReason.AddedInOctopus, true)),
                OctopusResourceKind.DeploymentSettings => new(OctopusImportPreviewAction.AlreadyImported, null),
                OctopusResourceKind.DeploymentProcess or OctopusResourceKind.VariableSet => new(OctopusImportPreviewAction.Skip, null, new OctopusImportDiagnosticDto { Code = OctopusImportPreviewDiagnosticCodes.NotInEarlierImport }),
                _ => new(OctopusImportPreviewAction.Create, null, Choice: new OctopusImportChoice(OctopusImportChoiceReason.AddedInOctopus, false))
            };

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
