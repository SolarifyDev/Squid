using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.OctopusImport.SessionFiles;

/// <summary>The paths an import session's files are stored under. Persisted format — pinned by tests.</summary>
public static class OctopusImportSessionFilePaths
{
    public const string StagingPrefix = "history-staging/";
    public const string DetailsPrefix = "history-details/";
    public const string Marker = "staging.id";

    /// <summary>The session's source file (its export upload, or the history bundle of a server session): the registered upload's file name.</summary>
    public static string SourceOf(OctopusImportSession session) => string.IsNullOrWhiteSpace(session?.TemporaryUploadPath) ? null : Path.GetFileName(session.TemporaryUploadPath);

    /// <summary>
    /// Refuses anything but a relative path of at most 512 characters with no ".." segment. ".." inside a name ("Octopus..export.zip")
    /// is an ordinary file name: an upload keeps the name it was sent with.
    /// </summary>
    public static void EnsureRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512 || path.StartsWith('/') || path.Split('/').Contains(".."))
            throw new ArgumentException("A relative session file path is required.", nameof(path));
    }
}
