namespace Squid.Message.Models.OctopusImport;

/// <summary>One file of an import session as the database keeps it. <see cref="Length"/> and <see cref="Sha256"/> describe the file's own bytes, before compression or encryption.</summary>
public sealed record OctopusImportSessionFileHeader(string Path, long Length, string Sha256, int ChunkCount, string Encoding, string Encryption);

/// <summary>What one write did: the new length, the length of the file it replaced (null when the path was new) and whether the path was new.</summary>
public sealed record OctopusImportSessionFileWrite(long Length, long? PreviousLength)
{
    public bool Created => PreviousLength == null;

    /// <summary>How many bytes the stored files grew by.</summary>
    public long Growth => Length - (PreviousLength ?? 0);
}

/// <summary>How many files a prefix holds and their total length.</summary>
public sealed record OctopusImportSessionFileTotals(long Bytes, int Files);

/// <summary>One whole file's bytes, read in path order.</summary>
public sealed record OctopusImportSessionFileContent(string Path, byte[] Content);
