namespace Squid.Core.Services.OctopusImport.Octopus;

public sealed record OctopusArchiveExtractionResult(IReadOnlyList<OctopusExtractedArchiveEntry> Entries)
{
    public int EntryCount => Entries.Count;

    public long TotalUncompressedSizeBytes => Entries.Sum(e => e.SizeBytes);
}

public sealed record OctopusExtractedArchiveEntry(string RelativePath, byte[] Content)
{
    public long SizeBytes => Content.LongLength;
}
