using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public partial class OctopusImportPreviewPlanner
{
    /// <summary>
    /// State of one preview build: the discovery inputs plus every decision made so far, so a resource's
    /// decision can read its owners' (they are decided first, in rank order).
    /// </summary>
    private sealed class PreviewPlanningContext
    {
        private readonly IReadOnlyDictionary<string, OctopusImportResourceConflict> _conflictsBySourceId;
        private readonly IReadOnlySet<string> _blockedSourceIds;
        private readonly IReadOnlySet<OctopusImportReleaseKey> _existingReleaseKeys;
        private readonly Dictionary<string, OctopusResourceNode> _nodesById;
        private readonly Dictionary<string, OctopusImportResourceResultDto> _previewsById = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, OctopusImportPreviewAction> _historyUnitActionsByTaskId = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _scopeSkippedIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly OctopusImportReferenceScope _referenceScope;
        private readonly Dictionary<int, IReadOnlyDictionary<string, int>> _variablesHeldBySet = new();

        public PreviewPlanningContext(OctopusImportDependencyPlan dependencyPlan, OctopusImportConflictDiscoveryResult conflicts, IReadOnlyList<OctopusResourceNode> nodes, OctopusImportChoiceSelections selections)
        {
            _conflictsBySourceId = conflicts.Conflicts.GroupBy(c => c.Source.SourceId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            _blockedSourceIds = dependencyPlan.Diagnostics.Where(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker && !string.IsNullOrWhiteSpace(d.SourceId)).Select(d => d.SourceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _existingReleaseKeys = conflicts.ExistingReleaseKeys;
            _nodesById = nodes.ToDictionary(n => n.SourceId, StringComparer.OrdinalIgnoreCase);
            _referenceScope = dependencyPlan.ReferenceScope ?? OctopusImportReferenceScope.Everything;
            Prior = conflicts.PriorMappings;
            Conflicts = conflicts;
            Nodes = nodes;
            Selections = selections;
        }

        /// <summary>What the user selected among the resources this plan offers to decide on.</summary>
        public OctopusImportChoiceSelections Selections { get; }

        public OctopusImportPriorMappings Prior { get; }

        public OctopusImportConflictDiscoveryResult Conflicts { get; }

        public IReadOnlyList<OctopusResourceNode> Nodes { get; }

        /// <summary>What happened to each still-missing secret this source speaks for, by diagnostic code (for the plan summary).</summary>
        public Dictionary<string, int> SecretFillTally { get; } = new(StringComparer.Ordinal);

        /// <summary>Missing secrets the file would write or settle: the ones that need installation evidence.</summary>
        public int SecretFillsNeedingEvidence { get; set; }

        private IReadOnlyDictionary<string, int> _variableKeyCounts;
        private OctopusImportFillEvidence _fillEvidence;
        private bool? _needsOriginProof;
        private IReadOnlyList<string> _otherOriginCreators;

        public IReadOnlyDictionary<string, int> VariableKeyCounts => _variableKeyCounts ??= OctopusImportSecretFills.VariableKeyCounts(Nodes, Prior);

        // A preview never carries the user's confirmation: a pairing is confirmed by posting the plan back to validation.
        public OctopusImportFillEvidence FillEvidence => _fillEvidence ??= OctopusImportFillEvidences.Evaluate(Conflicts.HistoryInstallationId, Conflicts.ExportSourceId, Nodes, Prior, pairingConfirmed: false);

        /// <summary>The source would link to rows an import of another origin created: it must prove the same Octopus.</summary>
        public bool NeedsOriginProof => _needsOriginProof ??= OctopusImportFillEvidences.NeedsOriginProof(Conflicts.HistoryInstallationId, Conflicts.ExportSourceId, Nodes, Prior);

        /// <summary>The origins (worded for messages) that created rows of a third origin the source would link to.</summary>
        public IReadOnlyList<string> OtherOriginCreators => _otherOriginCreators ??= OctopusImportOriginMessages.Creators(OctopusImportFillEvidences.OtherOriginCreators(Conflicts.HistoryInstallationId, Conflicts.ExportSourceId, Nodes, Prior), Conflicts.CreatorOrigins);

        /// <summary>
        /// The owner's Squid rows still hold this child, which has no live row of its own: by name, and for a variable by name and scope
        /// with each Squid row holding at most one Octopus variable (rows linked to another Octopus variable hold that one).
        /// </summary>
        public bool IsStillInSquid(OctopusResourceNode child, int ownerId)
            => child.Kind == OctopusResourceKind.Variable ? VariablesHeldBy(ownerId).ContainsKey(child.SourceId) : Conflicts.DestinationChildren.Any(c => c.Kind == child.Kind.ToString() && c.OwnerId == ownerId && c.Name == child.Name);

        private IReadOnlyDictionary<string, int> VariablesHeldBy(int variableSetId)
        {
            if (!_variablesHeldBySet.TryGetValue(variableSetId, out var held))
                _variablesHeldBySet[variableSetId] = held = OctopusImportVariableMatching.Pair(UnmappedVariablesOf(variableSetId), UnclaimedVariableRowsOf(variableSetId), PriorDestinationOf);

            return held;
        }

        private IEnumerable<OctopusResourceNode> UnmappedVariablesOf(int variableSetId)
            => Nodes.Where(n => n.Kind == OctopusResourceKind.Variable && PriorFor(n.Kind, n.SourceId) == null && OctopusImportChildOwners.ImportedOwnerDestinationId(n, Prior) == variableSetId);

        private IEnumerable<OctopusImportDestinationChild> UnclaimedVariableRowsOf(int variableSetId)
        {
            var variableType = OctopusImportPriorMappings.Normalize(nameof(OctopusResourceKind.Variable));
            var claimed = Prior.Mappings.Where(m => m.Key.Type == variableType).Select(m => m.Value.DestinationId).ToHashSet();

            return Conflicts.DestinationChildren.Where(c => c.Kind == nameof(OctopusResourceKind.Variable) && c.OwnerId == variableSetId && !claimed.Contains(c.Id));
        }

        private int? PriorDestinationOf(string sourceId, OctopusResourceKind kind) => PriorFor(kind, sourceId)?.DestinationId;

        public OctopusImportResourceResultDto Record(OctopusImportResourceResultDto preview)
        {
            _previewsById[preview.SourceId] = preview;
            return preview;
        }

        public bool IsBlocked(string sourceId) => _blockedSourceIds.Contains(sourceId);

        public bool IsReferenced(string sourceId) => _referenceScope.IsReferenced(sourceId);

        /// <summary>The session chose projects and this resource is neither one of them, theirs, nor used by them.</summary>
        public bool IsOutsideSelection(OctopusResourceNode resource) => _referenceScope.IsOutsideSelection(resource);

        public bool IsOutsideSelection(string sourceId) => IsOutsideSelection(FindNode(sourceId));

        /// <summary>A Config-as-Code project the import would otherwise take, or something it owns.</summary>
        public bool IsConfigAsCode(OctopusResourceNode resource) => _referenceScope.IsConfigAsCode(resource);

        /// <summary>Not imported because of the selection or because its project is Config as Code.</summary>
        public bool IsLeftOut(string sourceId) => _referenceScope.IsLeftOut(FindNode(sourceId));

        public IReadOnlyList<string> TargetsOf(string sourceId) => _referenceScope.TargetsOf(sourceId);

        /// <summary>Skipped by the whole-space scope rule: what it owns is skipped with it, and an unreferenced resource that uses it too.</summary>
        public void MarkScopeSkipped(string sourceId) => _scopeSkippedIds.Add(sourceId);

        public bool IsScopeSkipped(string sourceId) => sourceId != null && _scopeSkippedIds.Contains(sourceId);

        public OctopusResourceNode FindNode(string sourceId) => !string.IsNullOrWhiteSpace(sourceId) ? _nodesById.GetValueOrDefault(sourceId) : null;

        public bool TryGetConflict(string sourceId, out OctopusImportResourceConflict conflict) => _conflictsBySourceId.TryGetValue(sourceId, out conflict);

        public OctopusImportPreviewAction? PreviewActionOf(string sourceId) => _previewsById.GetValueOrDefault(sourceId)?.PreviewAction;

        /// <summary>The choice recorded on a resource already decided, or null when the user does not decide about it.</summary>
        public OctopusImportChoiceDto ChoiceOf(string sourceId) => string.IsNullOrWhiteSpace(sourceId) ? null : _previewsById.GetValueOrDefault(sourceId)?.Choice;

        public OctopusResourceNode FindNode(string sourceId, OctopusResourceKind kind)
            => !string.IsNullOrWhiteSpace(sourceId) && _nodesById.TryGetValue(sourceId, out var node) && node.Kind == kind ? node : null;

        public OctopusImportPriorMapping PriorFor(OctopusResourceKind kind, string sourceId)
            => Prior.TryGet(kind.ToString(), sourceId, out var mapping) ? mapping : null;

        /// <summary>The target maps to a Squid row after confirmation: this plan resolves it, or an earlier import recorded it.</summary>
        public bool Resolves(OctopusResourceKind kind, string sourceId)
        {
            if (string.IsNullOrWhiteSpace(sourceId)) return false;

            return IsResolvedByThisPlan(kind, sourceId) || PriorFor(kind, sourceId) != null;
        }

        public bool ReleaseVersionExists(OctopusReleaseDto release, string version)
        {
            var project = PriorFor(OctopusResourceKind.Project, release.ProjectId);
            var channelId = PriorFor(OctopusResourceKind.Channel, release.ChannelId)?.DestinationId ?? ReusedDestinationOf(release.ChannelId);

            return project != null && channelId != null && _existingReleaseKeys.Contains(new OctopusImportReleaseKey(project.DestinationId, channelId.Value, version));
        }

        // A channel this plan links to a Squid channel by name may already hold the incoming version.
        private int? ReusedDestinationOf(string sourceId)
            => _previewsById.GetValueOrDefault(sourceId ?? string.Empty) is { PreviewAction: OctopusImportPreviewAction.ReuseExisting } preview ? preview.DestinationId : null;

        public void RecordHistoryUnit(string taskSourceId, OctopusImportPreviewAction action) => _historyUnitActionsByTaskId.TryAdd(taskSourceId, action);

        public bool TryGetHistoryUnitAction(string taskSourceId, out OctopusImportPreviewAction action) => _historyUnitActionsByTaskId.TryGetValue(taskSourceId, out action);

        private bool IsResolvedByThisPlan(OctopusResourceKind kind, string sourceId)
            => _previewsById.TryGetValue(sourceId, out var preview)
               && string.Equals(preview.SourceType, kind.ToString(), StringComparison.Ordinal)
               && OctopusImportPreviewActions.ResolvesToDestination(preview.PreviewAction);
    }
}
