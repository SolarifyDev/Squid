using Squid.Core.Services.OctopusImport.Octopus;
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

        public PreviewPlanningContext(OctopusImportDependencyPlan dependencyPlan, OctopusImportConflictDiscoveryResult conflicts, IReadOnlyList<OctopusResourceNode> nodes)
        {
            _conflictsBySourceId = conflicts.Conflicts.GroupBy(c => c.Source.SourceId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            _blockedSourceIds = dependencyPlan.Diagnostics.Where(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker && !string.IsNullOrWhiteSpace(d.SourceId)).Select(d => d.SourceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _existingReleaseKeys = conflicts.ExistingReleaseKeys;
            _nodesById = nodes.ToDictionary(n => n.SourceId, StringComparer.OrdinalIgnoreCase);
            Prior = conflicts.PriorMappings;
        }

        public OctopusImportPriorMappings Prior { get; }

        public OctopusImportResourceResultDto Record(OctopusImportResourceResultDto preview)
        {
            _previewsById[preview.SourceId] = preview;
            return preview;
        }

        public bool IsBlocked(string sourceId) => _blockedSourceIds.Contains(sourceId);

        public bool TryGetConflict(string sourceId, out OctopusImportResourceConflict conflict) => _conflictsBySourceId.TryGetValue(sourceId, out conflict);

        public OctopusImportPreviewAction? PreviewActionOf(string sourceId) => _previewsById.GetValueOrDefault(sourceId)?.PreviewAction;

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
            var channel = PriorFor(OctopusResourceKind.Channel, release.ChannelId);

            return project != null && channel != null && _existingReleaseKeys.Contains(new OctopusImportReleaseKey(project.DestinationId, channel.DestinationId, version));
        }

        public void RecordHistoryUnit(string taskSourceId, OctopusImportPreviewAction action) => _historyUnitActionsByTaskId.TryAdd(taskSourceId, action);

        public bool TryGetHistoryUnitAction(string taskSourceId, out OctopusImportPreviewAction action) => _historyUnitActionsByTaskId.TryGetValue(taskSourceId, out action);

        private bool IsResolvedByThisPlan(OctopusResourceKind kind, string sourceId)
            => _previewsById.TryGetValue(sourceId, out var preview)
               && string.Equals(preview.SourceType, kind.ToString(), StringComparison.Ordinal)
               && OctopusImportPreviewActions.ResolvesToDestination(preview.PreviewAction);
    }
}
