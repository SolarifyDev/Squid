using System.IO;
using System.Linq;
using System.Text;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Octopus;

public class OctopusInputExtractorTests
{
    private readonly OctopusInputExtractor _extractor = new();

    [Fact]
    public async Task ExtractStandaloneJsonAsync_ProjectJson_ReturnsRecognizedDocument()
    {
        var result = await ExtractStandaloneAsync("""
        {
          "Id": "Projects-1",
          "Name": "Project"
        }
        """, "project.json");

        result.Documents.Count.ShouldBe(1);
        result.Documents[0].Classification.Kind.ShouldBe(OctopusDocumentKind.Project);
        result.Documents[0].Classification.SourceId.ShouldBe("Projects-1");
        result.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExtractStandaloneJsonAsync_ManifestJson_ReturnsManifestDocument()
    {
        var result = await ExtractStandaloneAsync("""
        {
          "Entries": []
        }
        """, "export.json");

        result.Documents.Count.ShouldBe(1);
        result.Documents[0].Classification.Kind.ShouldBe(OctopusDocumentKind.Manifest);
    }

    [Fact]
    public async Task ExtractStandaloneJsonAsync_MalformedJson_ReturnsStructuredDiagnostic()
    {
        var result = await ExtractStandaloneAsync("""{"Id":""", "bad.json");

        result.Documents.ShouldBeEmpty();
        result.Diagnostics.Count.ShouldBe(1);
        result.Diagnostics[0].Severity.ShouldBe(OctopusImportCompatibilitySeverity.Blocker);
        result.Diagnostics[0].Code.ShouldBe(OctopusInputExtractionDiagnosticCodes.MalformedJson);
        result.Diagnostics[0].Message.ShouldContain("bad.json");
        result.Diagnostics[0].Message.ShouldNotContain("""{"Id":""");
    }

    [Fact]
    public async Task ExtractStandaloneJsonAsync_UnrecognizedJson_ReturnsDiagnostic()
    {
        var result = await ExtractStandaloneAsync("""
        {
          "Name": "not an Octopus resource"
        }
        """, "unknown.json");

        result.Documents.ShouldBeEmpty();
        result.Diagnostics.Count.ShouldBe(1);
        result.Diagnostics[0].Severity.ShouldBe(OctopusImportCompatibilitySeverity.Warning);
        result.Diagnostics[0].Code.ShouldBe(OctopusInputExtractionDiagnosticCodes.UnrecognizedDocument);
    }

    [Fact]
    public async Task ExtractStandaloneJsonAsync_ArrayRoot_ReturnsUnsupportedRootDiagnostic()
    {
        var result = await ExtractStandaloneAsync("""[]""", "array.json");

        result.Documents.ShouldBeEmpty();
        result.Diagnostics.Single().Code.ShouldBe(OctopusInputExtractionDiagnosticCodes.UnsupportedJsonRoot);
    }

    [Fact]
    public async Task ExtractStandaloneJsonAsync_EntryLimitExceeded_ReturnsDiagnostic()
    {
        var result = await ExtractStandaloneAsync(
            """{"Id":"Projects-1"}""",
            "project.json",
            new OctopusArchiveExtractionOptions { MaxEntrySizeBytes = 4 });

        result.Documents.ShouldBeEmpty();
        result.Diagnostics.Single().Code.ShouldBe(OctopusInputExtractionDiagnosticCodes.EntrySizeLimitExceeded);
    }

    [Fact]
    public async Task ExtractFolderAsync_ReadsJsonFilesRecursivelyAndSkipsNonJson()
    {
        using var folder = TempFolder.Create();
        folder.WriteText("manifest.json", """{"Entries":[]}""");
        folder.WriteText("nested/Projects-1.json", """{"Id":"Projects-1"}""");
        folder.WriteText("readme.txt", "ignored");

        var result = await _extractor.ExtractFolderAsync(folder.Path);

        result.Documents.Count.ShouldBe(2);
        result.Documents.Select(d => d.SourcePath).ShouldBe(["manifest.json", "nested/Projects-1.json"]);
        result.Documents.Select(d => d.Classification.Kind).ShouldBe([OctopusDocumentKind.Manifest, OctopusDocumentKind.Project]);
        result.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExtractFolderAsync_MissingFolder_ReturnsDiagnostic()
    {
        var result = await _extractor.ExtractFolderAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        result.Documents.ShouldBeEmpty();
        result.Diagnostics.Single().Code.ShouldBe(OctopusInputExtractionDiagnosticCodes.FolderNotFound);
    }

    [Fact]
    public async Task ExtractFolderAsync_NoJson_ReturnsDiagnostic()
    {
        using var folder = TempFolder.Create();
        folder.WriteText("readme.txt", "ignored");

        var result = await _extractor.ExtractFolderAsync(folder.Path);

        result.Documents.ShouldBeEmpty();
        result.Diagnostics.Single().Code.ShouldBe(OctopusInputExtractionDiagnosticCodes.NoJsonDocuments);
    }

    [Fact]
    public async Task ExtractFolderAsync_EntryCountLimitExceeded_ReturnsDiagnostic()
    {
        using var folder = TempFolder.Create();
        folder.WriteText("one.json", "{}");
        folder.WriteText("two.json", "{}");

        var result = await _extractor.ExtractFolderAsync(
            folder.Path,
            new OctopusArchiveExtractionOptions { MaxEntryCount = 1 });

        result.Documents.ShouldBeEmpty();
        result.Diagnostics.Single().Code.ShouldBe(OctopusInputExtractionDiagnosticCodes.EntryCountLimitExceeded);
    }

    [Fact]
    public async Task ExtractFolderAsync_TotalSizeLimitExceeded_ReturnsDiagnostic()
    {
        using var folder = TempFolder.Create();
        folder.WriteText("one.json", """{"Id":"Projects-1"}""");
        folder.WriteText("two.json", """{"Id":"ProjectGroups-1"}""");

        var result = await _extractor.ExtractFolderAsync(
            folder.Path,
            new OctopusArchiveExtractionOptions
            {
                MaxEntrySizeBytes = 100,
                MaxTotalUncompressedSizeBytes = 30
            });

        result.Documents.Count.ShouldBe(1);
        result.Diagnostics.Single().Code.ShouldBe(OctopusInputExtractionDiagnosticCodes.TotalSizeLimitExceeded);
    }

    [Fact]
    public async Task ExtractJsonEntriesAsync_ParsesJsonEntriesAndSkipsNonJson()
    {
        var entries = new List<OctopusExtractedArchiveEntry>
        {
            new("manifest.json", JsonBytes("""{"Entries":[]}""")),
            new("notes.txt", Encoding.UTF8.GetBytes("ignored")),
            new("Projects-1.json", JsonBytes("""{"Id":"Projects-1"}"""))
        };

        var result = await _extractor.ExtractJsonEntriesAsync(entries);

        result.Documents.Count.ShouldBe(2);
        result.Documents.Select(d => d.Classification.Kind).ShouldBe([OctopusDocumentKind.Manifest, OctopusDocumentKind.Project]);
        result.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExtractStandaloneJsonAsync_CancellationRequested_StopsExtraction()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => ExtractStandaloneAsync("""{"Id":"Projects-1"}""", "project.json", ct: cts.Token));
    }

    private Task<OctopusInputExtractionResult> ExtractStandaloneAsync(
        string json,
        string sourcePath,
        OctopusArchiveExtractionOptions options = null,
        CancellationToken ct = default)
    {
        return _extractor.ExtractStandaloneJsonAsync(
            new MemoryStream(JsonBytes(json)),
            sourcePath,
            options,
            ct);
    }

    private static byte[] JsonBytes(string json) => Encoding.UTF8.GetBytes(json);

    private sealed class TempFolder : IDisposable
    {
        private TempFolder(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempFolder Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"squid-octopus-input-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempFolder(path);
        }

        public void WriteText(string relativePath, string text)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            var directory = System.IO.Path.GetDirectoryName(path);

            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, text);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
