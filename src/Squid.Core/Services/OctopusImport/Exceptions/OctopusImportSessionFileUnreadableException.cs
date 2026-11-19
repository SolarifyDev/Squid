namespace Squid.Core.Services.OctopusImport.Exceptions;

/// <summary>
/// A stored file of an import session cannot be read back intact: it was sealed under a key this server cannot unwrap (the master key
/// changed), or its bytes do not match what was stored. The file is treated as lost, never as partly readable. The message names no path.
/// </summary>
public class OctopusImportSessionFileUnreadableException : InvalidOperationException
{
    public OctopusImportSessionFileUnreadableException() : base("A stored file of this import session cannot be read back intact.")
    {
    }
}
