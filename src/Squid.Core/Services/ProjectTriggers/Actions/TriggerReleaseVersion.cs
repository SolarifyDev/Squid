using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.ProjectTriggers.Actions;

/// <summary>The default trigger policy starts at 1.0.0 and increments the highest stable major.minor.patch in the Channel.</summary>
internal static class TriggerReleaseVersion
{
    internal static async Task<string> AllocateAsync(
        ITriggerActionDataProvider dataProvider, Project project, int channelId, string supplied, CancellationToken ct)
    {
        // Serialise explicit versions with automatic trigger allocations too. ReleaseService and the database
        // unique index continue to detect collisions with manual releases written outside this lock.
        await dataProvider.LockReleaseVersionAsync(channelId, ct).ConfigureAwait(false);

        if (supplied != null)
        {
            if (string.IsNullOrWhiteSpace(supplied) || supplied.Length > 349)
                throw new TriggerActionRejectedException("A supplied release version must contain between 1 and 349 characters.");

            return supplied;
        }

        EnsureDefaultPolicy(project.Json);
        EnsureDefaultPolicy(project.DeploymentSettingsJson);

        var versions = await dataProvider.GetReleaseVersionsAsync(
            project.SpaceId, project.Id, channelId, ct).ConfigureAwait(false);

        return Next(versions);
    }

    internal static string Next(IReadOnlyList<string> versions)
    {
        if (versions.Count == 0) return "1.0.0";

        var highest = default(Version);

        foreach (var value in versions)
        {
            if (!Version.TryParse(value, out var version)
                || version.Build < 0 || version.Revision >= 0 || version.ToString(3) != value)
                throw new TriggerActionRejectedException("Automatic release versioning requires stable major.minor.patch versions in the selected Channel.");

            if (highest == null || version > highest) highest = version;
        }

        if (highest.Build == int.MaxValue)
            throw new TriggerActionRejectedException("Automatic release versioning cannot increment the patch component of this major.minor.patch version.");

        return new Version(highest.Major, highest.Minor, highest.Build + 1).ToString(3);
    }

    internal static void EnsureDefaultPolicy(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            CheckSettings(root);

            foreach (var property in root.EnumerateObject())
                if (property.Name.Equals("deploymentSettings", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind != JsonValueKind.Null)
                    CheckSettings(property.Value);
        }
        catch (JsonException)
        {
            throw new TriggerActionRejectedException("Cannot read project settings for automatic release versioning.");
        }
    }

    private static void CheckSettings(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object)
            throw new TriggerActionRejectedException("Cannot determine the project's automatic release versioning policy from its settings.");

        foreach (var property in settings.EnumerateObject())
        {
            // OctopusImportProjectMapper retains the presence of a strategy, but loses its template and donor.
            if ((property.Name.Equals("hasVersioningStrategy", StringComparison.OrdinalIgnoreCase)
                 && property.Value.ValueKind != JsonValueKind.False)
                || (property.Name.Equals("versioningStrategy", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind != JsonValueKind.Null))
                throw new TriggerActionRejectedException("Only default release versioning is supported. This project declares a versioning strategy that cannot be evaluated.");
        }
    }
}
