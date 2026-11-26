namespace Squid.Core.Services.DeploymentExecution.Packages.Sources;

internal enum PackageFeedKind { Other, Helm, GitHub, NuGet }

/// <summary>
/// Feed types are free-form strings matched by substring (e.g. "GitHub Repository Feed"). A name that
/// mentions several kinds resolves by one fixed precedence, so exactly one source claims any feed type.
/// </summary>
internal static class PackageFeedKinds
{
    public static PackageFeedKind Classify(string feedType)
    {
        if (Mentions(feedType, "Helm")) return PackageFeedKind.Helm;
        if (Mentions(feedType, "GitHub")) return PackageFeedKind.GitHub;
        if (Mentions(feedType, "NuGet")) return PackageFeedKind.NuGet;

        return PackageFeedKind.Other;
    }

    public static bool Mentions(string feedType, string token) => (feedType ?? string.Empty).Contains(token, StringComparison.OrdinalIgnoreCase);
}
