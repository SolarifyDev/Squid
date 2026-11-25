namespace Squid.Core.Services.Packages.Storage.Providers.FileSystem;

public static class FileSystemRootResolver
{
    public const string DefaultFolderName = "packages";

    /// <summary>
    /// The folder packages live in. An explicit root wins (a relative one is taken from <paramref name="baseDirectory"/>); an empty one means
    /// "decide at runtime": under the data directory when this install has one, otherwise beside the application.
    /// </summary>
    public static string Resolve(string configuredRoot, string dataDirectory, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot.Trim(), baseDirectory));

        var parent = string.IsNullOrWhiteSpace(dataDirectory) ? baseDirectory : dataDirectory;

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(parent, DefaultFolderName), baseDirectory));
    }

    /// <summary>
    /// A warning when the folder is inside the application's own folder, which an upgrade may replace, taking every package with it; null otherwise.
    /// A folder that only shares a name prefix with the application folder (<c>/app2</c> next to <c>/app</c>) is not inside it.
    /// </summary>
    public static string FindApplicationFolderWarning(string resolvedRoot, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(resolvedRoot) || string.IsNullOrWhiteSpace(baseDirectory)) return null;

        var application = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolvedRoot));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (!root.Equals(application, comparison) && !root.StartsWith(application + Path.DirectorySeparatorChar, comparison)) return null;

        return $"{root} is inside the application folder ({application}). An upgrade can replace that folder and delete every package stored there. Set a root folder outside the application folder, or configure a data directory.";
    }
}
