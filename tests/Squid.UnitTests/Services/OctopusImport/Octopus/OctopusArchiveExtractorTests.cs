using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.UnitTests.Services.OctopusImport.Octopus;

public class OctopusArchiveExtractorTests
{
    private readonly OctopusArchiveExtractor _extractor = new();

    [Fact]
    public async Task ExtractZipAsync_ValidZip_ReturnsNormalizedEntries()
    {
        var bytes = CreateZipArchive(new Dictionary<string, byte[]>
        {
            ["manifest.json"] = JsonBytes("""{"Entries":[]}"""),
            ["Projects-1.json"] = JsonBytes("""{"Id":"Projects-1","Name":"Project"}""")
        });

        var result = await ExtractAsync(bytes);

        result.EntryCount.ShouldBe(2);
        result.Entries[0].RelativePath.ShouldBe("manifest.json");
        result.Entries[1].RelativePath.ShouldBe("Projects-1.json");
        Encoding.UTF8.GetString(result.Entries[1].Content).ShouldContain("Projects-1");
    }

    [Fact]
    public async Task ExtractZipAsync_ManifestArchive_ReturnsManifestAndCurrentResourceFiles()
    {
        var bytes = CreateZipArchive(new Dictionary<string, byte[]>
        {
            ["manifest.json"] = JsonBytes("""
            {
              "Entries": [
                {
                  "Id": "Projects-1",
                  "Name": "Project",
                  "DocumentType": "Project",
                  "DocumentSource": "Projects-1.json",
                  "Hash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                }
              ]
            }
            """),
            ["Projects-1.json"] = JsonBytes("""{"Id":"Projects-1","Name":"Project"}"""),
            ["variableset-Projects-1.json"] = JsonBytes("""{"Id":"variableset-Projects-1","OwnerId":"Projects-1"}""")
        });

        var result = await ExtractAsync(bytes);

        result.EntryCount.ShouldBe(3);
        result.Entries.Select(entry => entry.RelativePath).ShouldBe([
            "manifest.json",
            "Projects-1.json",
            "variableset-Projects-1.json"
        ]);
        Encoding.UTF8.GetString(result.Entries.Single(entry => entry.RelativePath == "manifest.json").Content)
            .ShouldContain("\"Entries\"");
    }

    [Theory]
    [InlineData("../manifest.json")]
    [InlineData("folder/../../manifest.json")]
    [InlineData("/manifest.json")]
    [InlineData("C:/manifest.json")]
    [InlineData("folder//manifest.json")]
    [InlineData("folder/./manifest.json")]
    public async Task ExtractZipAsync_UnsafeEntryPath_RejectsArchive(string entryPath)
    {
        var bytes = CreateZipArchive(new Dictionary<string, byte[]>
        {
            [entryPath] = JsonBytes("""{"Entries":[]}""")
        });

        var ex = await Should.ThrowAsync<OctopusArchiveExtractionException>(() => ExtractAsync(bytes));

        ex.Code.ShouldBe(OctopusArchiveExtractionErrorCodes.EntryPathUnsafe);
    }

    [Fact]
    public async Task ExtractZipAsync_UnsafeDirectoryEntry_RejectsArchive()
    {
        var bytes = CreateZipArchiveWithDirectory("../");

        var ex = await Should.ThrowAsync<OctopusArchiveExtractionException>(() => ExtractAsync(bytes));

        ex.Code.ShouldBe(OctopusArchiveExtractionErrorCodes.EntryPathUnsafe);
    }

    [Fact]
    public async Task ExtractZipAsync_DuplicateNormalizedPath_RejectsArchive()
    {
        var bytes = CreateZipArchive(new Dictionary<string, byte[]>
        {
            ["manifest.json"] = JsonBytes("""{"Entries":[]}"""),
            ["MANIFEST.json"] = JsonBytes("""{"Entries":[]}""")
        });

        var ex = await Should.ThrowAsync<OctopusArchiveExtractionException>(() => ExtractAsync(bytes));

        ex.Code.ShouldBe(OctopusArchiveExtractionErrorCodes.DuplicateEntryPath);
    }

    [Theory]
    [InlineData("nested.zip")]
    [InlineData("nested.tar.gz")]
    [InlineData("nested.tgz")]
    public async Task ExtractZipAsync_NestedArchiveExtension_RejectsArchive(string entryPath)
    {
        var bytes = CreateZipArchive(new Dictionary<string, byte[]>
        {
            [entryPath] = Encoding.UTF8.GetBytes("not even opened as an archive")
        });

        var ex = await Should.ThrowAsync<OctopusArchiveExtractionException>(() => ExtractAsync(bytes));

        ex.Code.ShouldBe(OctopusArchiveExtractionErrorCodes.NestedArchiveRejected);
    }

    [Fact]
    public async Task ExtractZipAsync_NestedArchiveMagic_RejectsArchive()
    {
        var nestedZipBytes = CreateZipArchive(new Dictionary<string, byte[]>
        {
            ["inner.json"] = JsonBytes("""{"Id":"Projects-1"}""")
        });
        var bytes = CreateZipArchive(new Dictionary<string, byte[]>
        {
            ["looks-like-json.json"] = nestedZipBytes
        });

        var ex = await Should.ThrowAsync<OctopusArchiveExtractionException>(() => ExtractAsync(bytes));

        ex.Code.ShouldBe(OctopusArchiveExtractionErrorCodes.NestedArchiveRejected);
    }

    [Fact]
    public async Task ExtractZipAsync_EntryCountLimitExceeded_RejectsArchive()
    {
        var bytes = CreateZipArchive(new Dictionary<string, byte[]>
        {
            ["one.json"] = JsonBytes("{}"),
            ["two.json"] = JsonBytes("{}")
        });

        var ex = await Should.ThrowAsync<OctopusArchiveExtractionException>(
            () => ExtractAsync(bytes, new OctopusArchiveExtractionOptions { MaxEntryCount = 1 }));

        ex.Code.ShouldBe(OctopusArchiveExtractionErrorCodes.EntryCountLimitExceeded);
    }

    [Fact]
    public async Task ExtractZipAsync_PerEntryLimitExceeded_RejectsArchive()
    {
        var bytes = CreateZipArchive(new Dictionary<string, byte[]>
        {
            ["large.json"] = JsonBytes("""{"value":"1234567890"}""")
        });

        var ex = await Should.ThrowAsync<OctopusArchiveExtractionException>(
            () => ExtractAsync(bytes, new OctopusArchiveExtractionOptions { MaxEntrySizeBytes = 8 }));

        ex.Code.ShouldBe(OctopusArchiveExtractionErrorCodes.EntrySizeLimitExceeded);
    }

    [Fact]
    public async Task ExtractZipAsync_TotalUncompressedLimitExceeded_RejectsArchive()
    {
        var bytes = CreateZipArchive(new Dictionary<string, byte[]>
        {
            ["one.json"] = JsonBytes("""{"value":"12345"}"""),
            ["two.json"] = JsonBytes("""{"value":"67890"}""")
        });

        var ex = await Should.ThrowAsync<OctopusArchiveExtractionException>(
            () => ExtractAsync(bytes, new OctopusArchiveExtractionOptions
            {
                MaxEntrySizeBytes = 100,
                MaxTotalUncompressedSizeBytes = 20
            }));

        ex.Code.ShouldBe(OctopusArchiveExtractionErrorCodes.TotalSizeLimitExceeded);
    }

    [Fact]
    public async Task ExtractZipAsync_CancellationRequested_StopsExtraction()
    {
        var bytes = CreateZipArchive(new Dictionary<string, byte[]>
        {
            ["manifest.json"] = JsonBytes("""{"Entries":[]}""")
        });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => ExtractAsync(bytes, ct: cts.Token));
    }

    [Fact]
    public async Task ExtractZipAsync_InvalidZip_RejectsArchive()
    {
        var bytes = Encoding.UTF8.GetBytes("not a zip");

        var ex = await Should.ThrowAsync<OctopusArchiveExtractionException>(() => ExtractAsync(bytes));

        ex.Code.ShouldBe(OctopusArchiveExtractionErrorCodes.InvalidArchive);
    }

    private Task<OctopusArchiveExtractionResult> ExtractAsync(
        byte[] bytes,
        OctopusArchiveExtractionOptions options = null,
        CancellationToken ct = default)
    {
        return _extractor.ExtractZipAsync(new MemoryStream(bytes), options, ct);
    }

    private static byte[] CreateZipArchive(Dictionary<string, byte[]> entries)
    {
        using var stream = new MemoryStream();

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(path);
                using var entryStream = entry.Open();
                entryStream.Write(content);
            }
        }

        return stream.ToArray();
    }

    private static byte[] CreateZipArchiveWithDirectory(string directoryPath)
    {
        using var stream = new MemoryStream();

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry(directoryPath);
        }

        return stream.ToArray();
    }

    private static byte[] JsonBytes(string json) => Encoding.UTF8.GetBytes(json);
}
