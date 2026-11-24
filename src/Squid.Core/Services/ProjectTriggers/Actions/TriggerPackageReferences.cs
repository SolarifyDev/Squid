using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Snapshots;

namespace Squid.Core.Services.ProjectTriggers.Actions;

internal sealed record TriggerPackageReference(string ActionName, string PackageReferenceName, string PackageId, int FeedId);

internal static class TriggerPackageReferences
{
    internal static List<TriggerPackageReference> ReadAction(
        string action, IReadOnlyDictionary<string, string> properties, string actionType = null)
    {
        var references = new List<TriggerPackageReference>();
        var package = properties.GetValueOrDefault(SpecialVariables.Action.PackageId);
        var feed = properties.GetValueOrDefault(SpecialVariables.Action.PackageFeedId);

        if (string.Equals(actionType, SpecialVariables.ActionTypes.TentaclePackage, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(package) || !string.IsNullOrWhiteSpace(feed))
            references.Add(Bind(action, package, package, feed));

        var containers = properties.GetValueOrDefault(KubernetesProperties.Containers);

        if (!string.IsNullOrWhiteSpace(containers)) ReadContainers(action, containers, references);

        return references;
    }

    private static void ReadContainers(string action, string json, List<TriggerPackageReference> references)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException();

            foreach (var container in document.RootElement.EnumerateArray())
                ReadContainer(action, container, references);
        }
        catch (JsonException)
        {
            throw new TriggerActionRejectedException("Cannot read the deployment action's container package references.");
        }
    }

    private static void ReadContainer(
        string action, JsonElement container, List<TriggerPackageReference> references)
    {
        if (container.ValueKind != JsonValueKind.Object)
            throw new JsonException();

        var package = GetValue(container, KubernetesContainerPayloadProperties.PackageId);
        var feed = GetValue(container, KubernetesContainerPayloadProperties.FeedId);

        if (string.IsNullOrWhiteSpace(package) && string.IsNullOrWhiteSpace(feed)) return;

        references.Add(Bind(
            action, GetValue(container, KubernetesContainerPayloadProperties.Name) ?? string.Empty, package, feed));
    }

    private static string GetValue(JsonElement container, string property)
    {
        if (!container.TryGetProperty(property, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when property == KubernetesContainerPayloadProperties.FeedId => value.GetRawText(),
            _ => throw new JsonException()
        };
    }

    private static TriggerPackageReference Bind(string action, string name, string package, string feed)
    {
        if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(package)
            || package.Contains("#{", StringComparison.Ordinal)
            || !int.TryParse(feed, NumberStyles.None, CultureInfo.InvariantCulture, out var feedId) || feedId <= 0)
            throw new TriggerActionRejectedException("An applicable deployment action requires a concrete package ID and a bound external feed.");

        return new TriggerPackageReference(action, name, package, feedId);
    }

    internal static string Fingerprint(IEnumerable<TriggerPackageReference> references)
    {
        var ordered = references.OrderBy(p => p.ActionName, StringComparer.Ordinal)
            .ThenBy(p => p.PackageReferenceName, StringComparer.Ordinal)
            .ThenBy(p => p.PackageId, StringComparer.Ordinal)
            .ThenBy(p => p.FeedId);

        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(ordered)));
    }

    internal static void ValidateSnapshot(
        DeploymentProcessSnapshotDto snapshot, int channelId, string preparedFingerprint)
    {
        var references = snapshot.Data.StepSnapshots.Where(s => !s.IsDisabled)
            .SelectMany(s => s.ActionSnapshots)
            .Where(a => !a.IsDisabled && (a.Channels.Count == 0 || a.Channels.Contains(channelId)))
            .SelectMany(a => ReadAction(a.Name, a.Properties, a.ActionType)).ToList();

        EnsureFingerprint(references, preparedFingerprint);
    }

    internal static void EnsureFingerprint(
        IReadOnlyList<TriggerPackageReference> references, string preparedFingerprint)
    {
        if (references.Count == 0 && preparedFingerprint == null) return;

        if (Fingerprint(references) != preparedFingerprint)
            throw new TriggerActionRejectedException("The prepared package references do not match the deployment process. Resolve the release packages for this Channel again.");
    }
}
