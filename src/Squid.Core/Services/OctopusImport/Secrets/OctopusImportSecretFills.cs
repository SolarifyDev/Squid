using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Deployments.Variables;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Variable;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Secrets;

/// <summary>
/// Single source of truth for filling secrets an earlier import left <c>Missing</c> (values the Octopus server withheld). The preview
/// planner, the validator and the confirmation all ask it, so preview shows exactly what confirmation does. The rules: only a live
/// <c>Missing</c> row is ever considered (<c>Filled</c>/<c>Superseded</c> are terminal); the Squid value must still be empty (a value
/// set in Squid wins for ever: the row becomes <c>Superseded</c>); the source must hold a decryptable value (an empty one settles the
/// row as <c>Filled</c> with no write); and the file must be proven to come from this space's Octopus installation.
/// </summary>
public static class OctopusImportSecretFills
{
    public static bool IsFillableKind(OctopusResourceKind kind) => kind is OctopusResourceKind.Variable or OctopusResourceKind.Feed;

    /// <summary>How many current variables of the source share each secret key: more than one means the key cannot tell them apart.</summary>
    public static IReadOnlyDictionary<string, int> VariableKeyCounts(IEnumerable<OctopusResourceNode> nodes, OctopusImportPriorMappings prior)
        => (nodes ?? []).Where(n => n.Kind == OctopusResourceKind.Variable && !n.IsHistorical && IsSensitive(n))
            .Select(n => VariableKey(n, prior))
            .Where(key => key != null)
            .GroupBy(OctopusImportPriorMappings.Normalize, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    /// <summary>The still-missing secret this resource can speak for, or null when there is none (no live <c>Missing</c> row).</summary>
    public static OctopusImportSecretFillCandidate Candidate(OctopusResourceNode node, OctopusImportPriorMappings prior, OctopusExportSecrets secrets, IReadOnlyDictionary<string, int> variableKeyCounts)
    {
        if (node == null || node.IsHistorical) return null;

        prior ??= OctopusImportPriorMappings.Empty;

        return node.Kind switch
        {
            OctopusResourceKind.Variable => VariableCandidate(node, prior, secrets, variableKeyCounts),
            OctopusResourceKind.Feed => FeedCandidate(node, prior, secrets),
            _ => null
        };
    }

    /// <summary>The Squid state of the candidate's secret, read from the slots loaded for this import.</summary>
    public static SecretSlotState DestinationState(OctopusImportSecretFillCandidate candidate, OctopusImportSecretSlots slots)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        slots ??= OctopusImportSecretSlots.Empty;

        return candidate.Kind == OctopusResourceKind.Variable
            ? SensitiveVariableSlots.Find(slots.Variables, candidate.DestinationId, candidate.Identity).State
            : ExternalFeedCredentialSlots.StateOf(slots.Feeds.FirstOrDefault(f => f.FeedId == candidate.DestinationId), candidate.FillUsername);
    }

    /// <summary>The value is written: the source has one, Squid's is still empty, and the file is proven to be from this installation.</summary>
    public static bool Fills(OctopusImportSecretFillCandidate candidate, SecretSlotState destination, OctopusImportFillEvidenceState evidence)
        => WouldFill(candidate, destination) && evidence == OctopusImportFillEvidenceState.Proven;

    /// <summary>The source and Squid allow a fill; whether the file may supply it is the evidence's question.</summary>
    public static bool WouldFill(OctopusImportSecretFillCandidate candidate, SecretSlotState destination)
        => candidate?.Source == OctopusImportSecretFillSource.Fillable && destination == SecretSlotState.Empty;

    /// <summary>
    /// How the row is settled without writing the secret: <c>Superseded</c> once Squid holds a value or the variable is gone (whatever the
    /// source), <c>Filled</c> when a proven file says the value is empty in Octopus too. Null: the row stays <c>Missing</c>.
    /// </summary>
    public static string SettledWithoutWrite(OctopusImportSecretFillCandidate candidate, SecretSlotState destination, OctopusImportFillEvidenceState evidence)
    {
        if (candidate == null) return null;

        if (destination is SecretSlotState.AlreadySet or SecretSlotState.NotFound) return OctopusImportSourceMapQueries.SupersededOutcome;

        var emptyInOctopus = destination == SecretSlotState.Empty && candidate.Source == OctopusImportSecretFillSource.EmptyInOctopus;

        return emptyInOctopus && evidence == OctopusImportFillEvidenceState.Proven ? OctopusImportSourceMapQueries.FilledOutcome : null;
    }

    /// <summary>The live <c>Missing</c> rows of one secret type, as the Squid rows (variable sets / feeds) they point at.</summary>
    public static IReadOnlyList<int> MissingDestinations(OctopusImportPriorMappings prior, string secretSourceType)
    {
        var type = OctopusImportPriorMappings.Normalize(secretSourceType);

        return (prior ?? OctopusImportPriorMappings.Empty).Mappings.Where(m => m.Key.Type == type && m.Value.OutcomeState == OctopusImportSourceMapQueries.MissingOutcome).Select(m => m.Value.DestinationId).Distinct().ToList();
    }

    private static OctopusImportSecretFillCandidate VariableCandidate(OctopusResourceNode node, OctopusImportPriorMappings prior, OctopusExportSecrets secrets, IReadOnlyDictionary<string, int> variableKeyCounts)
    {
        if (!IsSensitive(node)) return null;

        var key = VariableKey(node, prior);

        if (key == null || !TryGetMissing(prior, OctopusImportSourceMapQueries.VariableSecretSourceType, key, out var row)) return null;

        var variable = node.GetSource<OctopusVariableDto>();
        var identity = new VariableIdentity(variable.Name, OctopusImportVariableMapper.SquidVariableType(variable) ?? VariableType.Password, OctopusImportSecretKeys.CanonicalScopes(variable.Scope, node.OwnerProjectId, DestinationOf(prior)));
        var ambiguous = (variableKeyCounts ?? new Dictionary<string, int>()).GetValueOrDefault(OctopusImportPriorMappings.Normalize(key)) > 1;

        return new OctopusImportSecretFillCandidate(OctopusResourceKind.Variable, OctopusImportSourceMapQueries.VariableSecretSourceType, key, row.DestinationId, identity, null, ambiguous ? OctopusImportSecretFillSource.Ambiguous : VariableSource(variable, secrets));
    }

    private static OctopusImportSecretFillCandidate FeedCandidate(OctopusResourceNode node, OctopusImportPriorMappings prior, OctopusExportSecrets secrets)
    {
        if (!TryGetMissing(prior, OctopusImportSourceMapQueries.FeedSecretSourceType, node.SourceId, out var row)) return null;

        var feed = node.GetSource<OctopusFeedDto>();
        var username = string.IsNullOrWhiteSpace(feed?.Username) ? null : feed.Username;

        return new OctopusImportSecretFillCandidate(OctopusResourceKind.Feed, OctopusImportSourceMapQueries.FeedSecretSourceType, node.SourceId, row.DestinationId, null, username, FeedSource(feed, secrets));
    }

    // A value carrying the "withheld" marker is no value at all: it never fills, and never settles the row as empty in Octopus.
    private static OctopusImportSecretFillSource VariableSource(OctopusVariableDto variable, OctopusExportSecrets secrets)
    {
        if (OctopusImportSensitiveValues.IsWithheld(variable)) return OctopusImportSecretFillSource.StillMissing;

        if (!OctopusImportSensitiveValues.TryResolve(variable, secrets, out var value)) return OctopusImportSecretFillSource.StillMissing;

        return string.IsNullOrEmpty(value) ? OctopusImportSecretFillSource.EmptyInOctopus : OctopusImportSecretFillSource.Fillable;
    }

    // A source without export secrets (an Octopus server, or no password) can never supply a feed password, even an "empty" one.
    private static OctopusImportSecretFillSource FeedSource(OctopusFeedDto feed, OctopusExportSecrets secrets)
    {
        if (feed == null || secrets?.IsAvailable != true) return OctopusImportSecretFillSource.StillMissing;

        var credentials = OctopusImportFeedCredentials.Resolve(feed, secrets);

        if (!credentials.IsResolved) return OctopusImportSecretFillSource.StillMissing;

        return string.IsNullOrEmpty(credentials.Password) ? OctopusImportSecretFillSource.EmptyInOctopus : OctopusImportSecretFillSource.Fillable;
    }

    private static string VariableKey(OctopusResourceNode node, OctopusImportPriorMappings prior)
        => OctopusImportSecretKeys.ForVariable(node.ParentSourceId, node.OwnerProjectId, node.GetSource<OctopusVariableDto>(), DestinationOf(prior));

    // Scope targets are compared by the Squid rows earlier imports mapped them to — exactly what the key was written with.
    private static Func<string, OctopusResourceKind, int?> DestinationOf(OctopusImportPriorMappings prior)
        => (sourceId, kind) => prior.TryGet(kind.ToString(), sourceId, out var mapping) ? mapping.DestinationId : null;

    private static bool IsSensitive(OctopusResourceNode node) => OctopusImportRequiredInputBuilder.IsSensitiveVariable(node.GetSource<OctopusVariableDto>());

    private static bool TryGetMissing(OctopusImportPriorMappings prior, string sourceType, string key, out OctopusImportPriorMapping row)
        => prior.TryGet(sourceType, key, out row) && row.OutcomeState == OctopusImportSourceMapQueries.MissingOutcome;
}
