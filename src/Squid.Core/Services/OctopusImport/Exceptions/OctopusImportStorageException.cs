namespace Squid.Core.Services.OctopusImport.Exceptions;

/// <summary>
/// This Squid server could not write or read the temporary files of an import session (most often a full disk). The message is fixed:
/// the I/O error names absolute server paths, so it is logged where it happens and never returned to the caller.
/// </summary>
public class OctopusImportStorageException : InvalidOperationException
{
    public OctopusImportStorageException() : base("This Squid server could not write the import's temporary files, most often because its disk is full. Nothing was imported and everything read so far is kept. An operator must free disk space on the volume of OctopusImport:TemporaryUploads:RootPath (OctopusImport__TemporaryUploads__RootPath), then resume.")
    {
    }
}
