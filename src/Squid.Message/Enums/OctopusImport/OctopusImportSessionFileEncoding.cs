namespace Squid.Message.Enums.OctopusImport;

/// <summary>How an import session file's bytes are stored: as they are, or deflate-compressed (staged JSON documents).</summary>
public enum OctopusImportSessionFileEncoding
{
    Identity,
    Deflate
}
