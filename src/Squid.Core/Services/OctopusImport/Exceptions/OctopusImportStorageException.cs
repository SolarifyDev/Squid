namespace Squid.Core.Services.OctopusImport.Exceptions;

/// <summary>
/// This Squid server could not write or read the temporary files of an import session (most often a full disk), or its database the
/// session's stored files. The message is fixed:
/// the I/O error names absolute server paths, so it is logged where it happens and never returned to the caller.
/// </summary>
public class OctopusImportStorageException : InvalidOperationException
{
    public const string DatabaseMessage = "This Squid server could not store the import's data in its database. Nothing was imported and everything read so far is kept; resume once the database is reachable and has space.";

    public OctopusImportStorageException() : base("This Squid server could not write the import's temporary files, most often because its disk is full. Nothing was imported and everything read so far is kept. An operator must free disk space on the volume of OctopusImport:TemporaryUploads:RootPath (OctopusImport__TemporaryUploads__RootPath), then resume.")
    {
    }

    private OctopusImportStorageException(string message) : base(message)
    {
    }

    /// <summary>The database refused or lost a write or read of the session's files; its own message is logged, never returned.</summary>
    public static OctopusImportStorageException Database() => new(DatabaseMessage);
}
