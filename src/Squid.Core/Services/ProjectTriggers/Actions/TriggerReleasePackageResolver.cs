using System.Text.RegularExpressions;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization.Exceptions;
using Squid.Core.Services.Identity;
using Squid.Core.Services.ProjectTriggers.Management;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Core.Services.Deployments.Channels;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;
using Squid.Message.Commands.Deployments.Release;

namespace Squid.Core.Services.ProjectTriggers.Actions;

public interface ITriggerReleasePackageResolver : IScopedDependency
{
    /// <summary>Before recording a Run, pin every applicable package to a version from its bound feed. Preselected observations must match the feed and Channel.</summary>
    Task PrepareAsync(ProjectTriggerDefinition definition, TriggerActionPlan plan, CancellationToken ct);
}

public sealed class TriggerReleasePackageResolver : ITriggerReleasePackageResolver
{
    private readonly ITriggerActionDataProvider _dataProvider;
    private readonly IExternalFeedPackageVersionService _versions;
    private readonly IChannelVersionRuleDataProvider _channelRules;
    private readonly IChannelVersionRuleValidator _validator;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectTriggerAccess _access;

    public TriggerReleasePackageResolver(
        ITriggerActionDataProvider dataProvider,
        IExternalFeedPackageVersionService versions,
        IChannelVersionRuleDataProvider channelRules,
        IChannelVersionRuleValidator validator,
        ICurrentUser currentUser,
        IProjectTriggerAccess access)
    {
        _dataProvider = dataProvider;
        _versions = versions;
        _channelRules = channelRules;
        _validator = validator;
        _currentUser = currentUser;
        _access = access;
    }

    public async Task PrepareAsync(ProjectTriggerDefinition definition, TriggerActionPlan plan, CancellationToken ct)
    {
        var inputs = await LoadInputsAsync(definition, plan, ct);

        if (inputs.References.Count > 0 && _dataProvider.HasActiveTransaction)
            throw new InvalidOperationException("External feed versions must be resolved before the trigger's business transaction.");

        var supplied = ValidatePins(inputs, plan.SelectedPackages);
        var selections = await ResolveSelectionsAsync(inputs, supplied, ct);

        plan.SelectedPackages = selections;
        plan.PackageReferenceFingerprint = TriggerPackageReferences.Fingerprint(inputs.References);
    }

    private async Task<List<CreateReleaseSelectedPackageDto>> ResolveSelectionsAsync(
        Inputs inputs, Dictionary<(string, string), CreateReleaseSelectedPackageDto> supplied, CancellationToken ct)
    {
        var selections = new List<CreateReleaseSelectedPackageDto>();
        var available = new Dictionary<(int FeedId, string PackageId), List<string>>();

        foreach (var reference in inputs.References)
        {
            var candidates = await GetAvailableVersionsAsync(reference, available, ct).ConfigureAwait(false);

            supplied.TryGetValue(Key(reference.ActionName, reference.PackageReferenceName), out var observed);

            var version = candidates.FirstOrDefault(candidate => (observed == null || candidate == observed.Version)
                && IsAllowed(reference.ActionName, candidate, inputs.Rules));

            if (version == null)
                throw new TriggerActionRejectedException("No version from the bound feed satisfies the observed package selection and Channel rules.");

            selections.Add(CreateSelection(reference, version));
        }

        return selections;
    }

    private static CreateReleaseSelectedPackageDto CreateSelection(TriggerPackageReference reference, string version)
    {
        return new CreateReleaseSelectedPackageDto
        {
            ActionName = reference.ActionName,
            PackageReferenceName = reference.PackageReferenceName,
            FeedId = reference.FeedId,
            Version = version
        };
    }

    private async Task<List<string>> GetAvailableVersionsAsync(
        TriggerPackageReference reference,
        Dictionary<(int FeedId, string PackageId), List<string>> available,
        CancellationToken ct)
    {
        var key = (reference.FeedId, reference.PackageId);

        if (available.TryGetValue(key, out var candidates)) return candidates;

        var response = await _versions.ListVersionsAsync(
            reference.FeedId,
            reference.PackageId,
            PackageVersionEnumerationCap.Resolve(),
            true,
            null,
            ct).ConfigureAwait(false);

        available[key] = response.Versions;

        return response.Versions;
    }

    // Called under the business transaction. Only stored references, pins and Channel rules are read here.
    internal async Task ValidateAsync(ProjectTriggerDefinition definition, TriggerActionPlan plan, CancellationToken ct)
    {
        var inputs = await LoadInputsAsync(definition, plan, ct).ConfigureAwait(false);

        TriggerPackageReferences.EnsureFingerprint(inputs.References, plan.PackageReferenceFingerprint);

        var supplied = ValidatePins(inputs, plan.SelectedPackages);

        if (supplied.Count != inputs.References.Count)
            throw new TriggerActionRejectedException("A selected version is required for every applicable package reference.");
    }

    private async Task<Inputs> LoadInputsAsync(
        ProjectTriggerDefinition definition, TriggerActionPlan plan, CancellationToken ct)
    {
        var processId = await _dataProvider.GetReleaseProcessIdAsync(
            definition.SpaceId, definition.ProjectId, plan.ChannelId, ct).ConfigureAwait(false)
            ?? throw new TriggerActionRejectedException("A release requires a Channel and deployment process owned by the enabled Project in the trigger's Space.");
        var references = await ReadReferencesAsync(processId, plan.ChannelId.Value, ct).ConfigureAwait(false);

        if (references.Count == 0) return new Inputs(references, []);

        await EnsureFeedAccessAsync(definition, references, ct).ConfigureAwait(false);

        var rules = await _channelRules.GetRulesByChannelIdAsync(plan.ChannelId.Value, ct).ConfigureAwait(false);

        ValidateRuleSyntax(rules);

        return new Inputs(references, rules);
    }

    private async Task EnsureFeedAccessAsync(
        ProjectTriggerDefinition definition, List<TriggerPackageReference> references, CancellationToken ct)
    {
        if (_currentUser.IsInternal || _currentUser.Id is not int userId
            || userId <= 0 || userId == CurrentUsers.InternalUser.Id)
            throw new PermissionDeniedException(Permission.FeedView, "A bound execution user is required.");

        await _access.EnsureProjectAsync(
            userId, definition.ProjectId, definition.SpaceId, Permission.FeedView, ct).ConfigureAwait(false);

        var feedIds = references.Select(r => r.FeedId).Distinct().ToArray();
        var count = await _dataProvider.CountFeedsAsync(definition.SpaceId, feedIds, ct).ConfigureAwait(false);

        if (count != feedIds.Length)
            throw new TriggerActionRejectedException("A selected package references an external feed outside the trigger's Space or a feed that no longer exists.");
    }

    private async Task<List<TriggerPackageReference>> ReadReferencesAsync(
        int processId, int channelId, CancellationToken ct)
    {
        var actions = await _dataProvider.GetApplicablePackageActionsAsync(
            processId, channelId, ct).ConfigureAwait(false);
        var ids = actions.Select(a => a.Id).ToArray();
        var properties = (await _dataProvider.GetActionPropertiesAsync(ids, ct).ConfigureAwait(false))
            .ToLookup(p => p.ActionId);

        return ReadActionReferences(actions, properties);
    }

    private static List<TriggerPackageReference> ReadActionReferences(
        List<TriggerPackageAction> actions, ILookup<int, DeploymentActionProperty> properties)
    {
        var result = new List<TriggerPackageReference>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var action in actions)
        {
            var actionReferences = TriggerPackageReferences.ReadAction(
                action.Name,
                properties[action.Id].ToDictionary(p => p.PropertyName, p => p.PropertyValue),
                action.ActionType);

            if (actionReferences.Count == 0) continue;

            if (!names.Add(action.Name)
                || actionReferences.Select(r => r.PackageReferenceName)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != actionReferences.Count)
                throw new TriggerActionRejectedException("Two applicable deployment actions or package references have ambiguous names.");

            result.AddRange(actionReferences);
        }

        return result;
    }

    private Dictionary<(string, string), CreateReleaseSelectedPackageDto> ValidatePins(
        Inputs inputs, List<CreateReleaseSelectedPackageDto> selections)
    {
        var expected = inputs.References.ToDictionary(r => Key(r.ActionName, r.PackageReferenceName));
        var supplied = new Dictionary<(string, string), CreateReleaseSelectedPackageDto>();

        foreach (var selection in selections ?? [])
        {
            if (selection == null)
                throw new TriggerActionRejectedException("A selected package cannot be null.");

            var key = Key(selection.ActionName, selection.PackageReferenceName);

            if (!expected.TryGetValue(key, out var reference)
                || reference.FeedId != selection.FeedId || !supplied.TryAdd(key, selection))
                throw new TriggerActionRejectedException("A selected package is duplicated or does not match a bound action, package reference and feed in this Channel.");

            if (!IsAllowed(reference.ActionName, selection.Version, inputs.Rules))
                throw new TriggerActionRejectedException("A selected package version is missing or does not satisfy the Channel rules for its action.");
        }

        return supplied;
    }

    private static (string, string) Key(string action, string reference)
    {
        return (action?.ToUpperInvariant(), (reference ?? string.Empty).ToUpperInvariant());
    }

    private bool IsAllowed(string action, string version, IReadOnlyList<ChannelVersionRule> rules)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 200) return false;

        // The manual validator accepts arbitrary container tags. A trigger must prove that a constrained tag matches.
        var constrained = rules.Any(rule => (!string.IsNullOrWhiteSpace(rule.VersionRange)
            || !string.IsNullOrWhiteSpace(rule.PreReleaseTag))
            && (string.IsNullOrWhiteSpace(rule.ActionNames)
                || rule.ActionNames.Split(',', StringSplitOptions.TrimEntries)
                    .Contains(action, StringComparer.OrdinalIgnoreCase)));

        return (!constrained || ChannelVersionRuleValidator.TryParseVersion(version, out _))
            && _validator.Validate(rules, [new SelectedPackageInfo(action, version)]).Count == 0;
    }

    private static void ValidateRuleSyntax(IReadOnlyList<ChannelVersionRule> rules)
    {
        foreach (var rule in rules)
        {
            ValidateVersionRange(rule.VersionRange);
            ValidatePreReleaseExpression(rule.PreReleaseTag);
        }
    }

    private static void ValidateVersionRange(string versionRange)
    {
        var range = versionRange?.Trim();

        if (!string.IsNullOrEmpty(range)
            && ((range[0] is '[' or '(' && (range.Length < 3 || range[^1] is not (']' or ')')))
                || !ChannelVersionRuleValidator.TryParseRange(range, out _, out _, out _, out _)))
            throw new TriggerActionRejectedException("A Channel rule contains an invalid package version range.");
    }

    private static void ValidatePreReleaseExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return;

        try
        {
            _ = new Regex(expression, RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (RegexParseException)
        {
            throw new TriggerActionRejectedException("A Channel rule contains an invalid pre-release expression.");
        }
    }

    private sealed record Inputs(List<TriggerPackageReference> References, List<ChannelVersionRule> Rules);
}
