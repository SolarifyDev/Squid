using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Pure re-import policy: how the source map recorded by earlier confirmations changes one resource's
/// preview action. Configuration that was imported before is never rewritten (Squid owns it after the
/// first import); only history is updated in place. A <c>null</c> decision means "no prior influence":
/// the planner keeps its existing conflict-based behaviour. The confirm stale check reuses this policy.
/// </summary>
public static class OctopusImportPriorMappingPolicy
{
    // Configuration kinds a confirmation records in the source map.
    private static readonly HashSet<OctopusResourceKind> MappedConfigurationKinds =
    [
        OctopusResourceKind.ProjectGroup, OctopusResourceKind.Environment, OctopusResourceKind.Lifecycle, OctopusResourceKind.LifecyclePhase,
        OctopusResourceKind.Feed, OctopusResourceKind.Account, OctopusResourceKind.Team, OctopusResourceKind.LibraryVariableSet,
        OctopusResourceKind.Project, OctopusResourceKind.Channel, OctopusResourceKind.VariableSet, OctopusResourceKind.Variable,
        OctopusResourceKind.DeploymentProcess, OctopusResourceKind.DeploymentStep, OctopusResourceKind.DeploymentAction
    ];

    // Kinds that live inside an owner (project, library variable set or lifecycle) and inherit its "already imported".
    private static readonly HashSet<OctopusResourceKind> OwnedKinds =
    [
        OctopusResourceKind.LifecyclePhase, OctopusResourceKind.Channel, OctopusResourceKind.DeploymentSettings, OctopusResourceKind.DeploymentProcess,
        OctopusResourceKind.DeploymentStep, OctopusResourceKind.DeploymentAction, OctopusResourceKind.VariableSet, OctopusResourceKind.Variable
    ];

    public static OctopusImportPriorDecision Decide(OctopusResourceNode node, OctopusImportPriorMappings prior, Func<string, OctopusImportPreviewAction?> ownerActionOf)
    {
        ArgumentNullException.ThrowIfNull(node);

        prior ??= OctopusImportPriorMappings.Empty;

        if (node.Kind == OctopusResourceKind.Release) return DecideRelease(node, prior);

        if (MappedConfigurationKinds.Contains(node.Kind) && TryGetPrior(prior, node, out var mapping)) return AlreadyImported(node, mapping);

        return IsUnderImportedOwner(node, ownerActionOf) ? DecideUnderImportedOwner(node) : null;
    }

    /// <summary>The owner an owned resource inherits from: its structural parent (lifecycle, library, variable set, process, step), else its project.</summary>
    public static string OwnerSourceId(OctopusResourceNode node)
        => OwnedKinds.Contains(node.Kind) ? node.ParentSourceId ?? node.OwnerProjectId : null;

    /// <summary>
    /// A Deployment and its ServerTask are one history unit with one stored fingerprint (on the Deployment).
    /// Task details are monotonic: an incoming document without details never counts as a change of details.
    /// </summary>
    public static OctopusImportPreviewAction DecideHistoryUnit(OctopusImportPriorMapping stored, OctopusServerTaskDto task)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (stored == null) return OctopusImportPreviewAction.Create;

        var sameTask = string.Equals(stored.Fingerprint, OctopusImportFingerprint.ForTask(task), StringComparison.Ordinal);
        var sameDetails = string.IsNullOrWhiteSpace(task.DetailFingerprint) || string.Equals(stored.DetailFingerprint, task.DetailFingerprint, StringComparison.Ordinal);

        return sameTask && sameDetails ? OctopusImportPreviewAction.AlreadyImported : OctopusImportPreviewAction.Update;
    }

    /// <summary>
    /// History from a second Octopus server must never be silently skipped as "already imported", so a source whose installation
    /// differs from the one this space was imported from blocks, naming both. Export files carry no installation id and are checked
    /// by <see cref="OctopusImportFillEvidences"/> instead.
    /// </summary>
    public static OctopusImportDiagnosticDto InstallationMismatch(string incomingInstallationId, OctopusImportPriorMappings prior, OctopusImportInstallationOrigin recordedOrigin = null, OctopusImportInstallationOrigin sourceOrigin = null)
    {
        if (!OctopusImportFillEvidences.IsAnotherInstallation(incomingInstallationId, prior)) return null;

        var message = OctopusImportOriginMessages.InstallationMismatch(OctopusImportOriginMessages.Source(incomingInstallationId, null, sourceOrigin), OctopusImportOriginMessages.Recorded(prior, recordedOrigin));

        return Diagnostic(OctopusImportCompatibilitySeverity.Blocker, OctopusImportPreviewDiagnosticCodes.OctopusInstallationMismatch, message, null);
    }

    /// <summary>
    /// A release update may rewrite Squid's notes only while they are still what the last import wrote; notes edited or cleared in Squid are
    /// the user's. Without a recorded fingerprint (an older import) only empty notes can be told apart from an edit.
    /// </summary>
    public static bool ReleaseNotesEditedInSquid(OctopusImportPriorMapping stored, string squidNotes)
        => stored?.Fingerprint == null
            ? !string.IsNullOrWhiteSpace(squidNotes)
            : !string.Equals(stored.Fingerprint, OctopusImportFingerprint.ForRelease(new OctopusReleaseDto { ReleaseNotes = squidNotes }), StringComparison.Ordinal);

    /// <summary>What an Update is previewed against: the recorded fingerprints of the history it rewrites (null when nothing is recorded).</summary>
    public static string ImportBasis(OctopusImportPriorMapping stored)
        => stored == null ? null : $"{stored.Fingerprint}|{stored.DetailFingerprint}";

    private static OctopusImportPriorDecision DecideRelease(OctopusResourceNode node, OctopusImportPriorMappings prior)
    {
        if (!TryGetPrior(prior, node, out var mapping)) return null;

        var unchanged = mapping.Fingerprint != null && mapping.Fingerprint == OctopusImportFingerprint.ForRelease(node.GetSource<OctopusReleaseDto>());

        return new OctopusImportPriorDecision(unchanged ? OctopusImportPreviewAction.AlreadyImported : OctopusImportPreviewAction.Update, mapping.DestinationId);
    }

    private static OctopusImportPriorDecision AlreadyImported(OctopusResourceNode node, OctopusImportPriorMapping mapping)
        => new(OctopusImportPreviewAction.AlreadyImported, mapping.DestinationId, HasConfigChanged(node, mapping) ? ChangedSinceLastImport(node) : null);

    private static bool HasConfigChanged(OctopusResourceNode node, OctopusImportPriorMapping mapping)
    {
        var incoming = OctopusImportFingerprint.ForConfig(node.GetSource<OctopusDocumentDto>());

        return mapping.Fingerprint != null && incoming != null && mapping.Fingerprint != incoming;
    }

    private static bool IsUnderImportedOwner(OctopusResourceNode node, Func<string, OctopusImportPreviewAction?> ownerActionOf)
    {
        var ownerId = OwnerSourceId(node);

        return !string.IsNullOrWhiteSpace(ownerId) && ownerActionOf?.Invoke(ownerId) == OctopusImportPreviewAction.AlreadyImported;
    }

    // A channel added in Octopus after the first import is created under the existing project. The default channel
    // is never remapped (that would overwrite the project's default channel): confirmation creates it as a regular
    // channel, so the releases and deployments on Octopus' new default channel are still imported.
    // Any other child without a live row of its own (a step, action, phase, variable) is not something the owner's import wrote
    // into Squid that Squid still holds: it was added in Octopus since, or removed in Squid. Imported configuration is never
    // changed, so it is skipped with that reason, never counted as "already in Squid". Deployment settings have no rows of
    // their own: they are what the owner's import wrote. (A variable's row also goes stale when its set is saved in Squid;
    // the planner keeps one Squid still holds by name.)
    private static OctopusImportPriorDecision DecideUnderImportedOwner(OctopusResourceNode node)
    {
        if (node.Kind == OctopusResourceKind.DeploymentSettings) return new OctopusImportPriorDecision(OctopusImportPreviewAction.AlreadyImported, null);
        if (node.Kind != OctopusResourceKind.Channel) return new OctopusImportPriorDecision(OctopusImportPreviewAction.Skip, null, NotInEarlierImport(node));

        var isDefault = node.GetSource<OctopusChannelDto>()?.IsDefault == true;

        return new OctopusImportPriorDecision(OctopusImportPreviewAction.Create, null, isDefault ? DefaultChannelNotRemapped(node) : null);
    }

    private static bool TryGetPrior(OctopusImportPriorMappings prior, OctopusResourceNode node, out OctopusImportPriorMapping mapping)
        => prior.TryGet(node.Kind.ToString(), node.SourceId, out mapping);

    private static OctopusImportDiagnosticDto ChangedSinceLastImport(OctopusResourceNode node)
        => Diagnostic(OctopusImportCompatibilitySeverity.Info, OctopusImportPreviewDiagnosticCodes.ChangedSinceLastImport, $"This {node.Kind} changed in Octopus since it was imported. Imported configuration is never overwritten; apply the change in Squid if it is still wanted.", node);

    private static OctopusImportDiagnosticDto NotInEarlierImport(OctopusResourceNode node)
        => Diagnostic(OctopusImportCompatibilitySeverity.Warning, OctopusImportPreviewDiagnosticCodes.NotInEarlierImport, $"This {node.Kind} is not in Squid: it was added in Octopus after what it belongs to was imported, or it was removed in Squid. Imported configuration is never changed, so it is not imported into the existing Squid resource; add it in Squid if it is still wanted.", node);

    private static OctopusImportDiagnosticDto DefaultChannelNotRemapped(OctopusResourceNode node)
        => Diagnostic(OctopusImportCompatibilitySeverity.Info, OctopusImportPreviewDiagnosticCodes.DefaultChannelNotRemapped, "The project was imported before and keeps its default channel; this Octopus default channel is created as a regular channel.", node);

    private static OctopusImportDiagnosticDto Diagnostic(OctopusImportCompatibilitySeverity severity, string code, string message, OctopusResourceNode node)
        => OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto { Severity = severity, Code = code, Message = message, SourceId = node?.SourceId, ResourceType = node?.Kind.ToString(), ResourceName = node?.Name });
}
