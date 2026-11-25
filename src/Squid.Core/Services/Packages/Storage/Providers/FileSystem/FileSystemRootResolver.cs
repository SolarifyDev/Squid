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
}
