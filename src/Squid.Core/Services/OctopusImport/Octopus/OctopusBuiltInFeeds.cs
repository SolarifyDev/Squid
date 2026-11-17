using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Octopus;

/// <summary>
/// Octopus's own package repositories: the built-in package repository and the project-release feed. Squid has no
/// equivalent, so they are never imported from either source — the REST normaliser drops them, the preview skips one
/// found in an export, and a step or release that pulls packages from one blocks with <see cref="ReferencedMessage"/>.
/// </summary>
public static class OctopusBuiltInFeeds
{
    public static IReadOnlyList<string> FeedTypes { get; } = ["BuiltIn", "OctopusProject"];

    // The fixed ids Octopus gives them in every space (REST and export alike, before the export suffix).
    public static IReadOnlyList<string> FeedIds { get; } = ["feeds-builtin", "feeds-builtin-releases"];

    public const string ReferencedMessage = "This uses Octopus's built-in package repository, which Squid cannot import (Squid has no built-in package repository). Push the packages to an external feed (NuGet, Docker, Helm or GitHub), point the step at that feed in Octopus, and import again.";

    public const string ExcludedMessage = "Octopus's built-in package repository is not imported: Squid has no built-in package repository and no imported project uses it.";

    public static bool IsBuiltInType(string feedType)
        => !string.IsNullOrWhiteSpace(feedType) && FeedTypes.Contains(feedType.Trim(), StringComparer.OrdinalIgnoreCase);

    public static bool IsBuiltInId(string feedId)
        => !string.IsNullOrWhiteSpace(feedId) && FeedIds.Contains(OctopusImportSourceIds.StripExportSuffix(feedId.Trim()), StringComparer.OrdinalIgnoreCase);
}
