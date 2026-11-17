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
}
