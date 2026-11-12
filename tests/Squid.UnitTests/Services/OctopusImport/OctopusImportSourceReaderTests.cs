using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public sealed class OctopusImportSourceReaderTests : IDisposable
{
    private readonly string _sessionDirectory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"squid-source-reader-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public async Task Merge_HistoryOverridesExportSameId_ManifestsMerged()
    {
        var exportPath = WriteZip("export.zip", ("Projects/Projects-1.json", new { Id = "Projects-1", Name = "Web" }, "Project"), ("Releases/Releases-1.json", new { Id = "Releases-1", ProjectId = "Projects-1", Version = "1.0.0", ReleaseNotes = "from the export" }, "Release"));
        var writer = new OctopusHistoryBundleWriter();
        await writer.WriteDocumentAsync(_sessionDirectory, OctopusResourceKind.Release, new OctopusReleaseDto { Id = "Releases-1", ProjectId = "Projects-1", Version = "1.0.0", ReleaseNotes = "from the server" });
        await writer.WriteDocumentAsync(_sessionDirectory, OctopusResourceKind.Deployment, new OctopusDeploymentDto { Id = "Deployments-1", ProjectId = "Projects-1", ReleaseId = "Releases-1" });
        await writer.BuildBundleAsync(_sessionDirectory, "inst-1");

        var extraction = await Reader().ReadAsync(exportPath);
        var inventory = new OctopusManifestInventoryBuilder().Build(extraction);
        var graph = new OctopusResourceGraphBuilder().Build(inventory);

        inventory.Diagnostics.ShouldBeEmpty();
        inventory.Manifest.InstallationId.ShouldBe("inst-1");
        inventory.Items.Select(i => i.ManifestEntry.Id).ShouldBe(["Releases-1", "Deployments-1", "Projects-1"], ignoreOrder: true);
        extraction.Documents.Count(d => d.Classification.Kind == OctopusDocumentKind.Manifest).ShouldBe(1);
        extraction.Documents.Count(d => d.Classification.SourceId == "Releases-1").ShouldBe(1);
        graph.Resources.Single(r => r.SourceId == "Releases-1").GetSource<OctopusReleaseDto>().ReleaseNotes.ShouldBe("from the server");
        graph.Resources.ShouldContain(r => r.SourceId == "Projects-1");
        inventory.Items.Where(i => i.Classification.Kind != OctopusDocumentKind.Project).ShouldAllBe(i => i.ManifestEntry.DocumentSource.StartsWith(OctopusImportSourceMerge.BundlePathPrefix));
    }

    [Fact]
    public async Task NoHistoryBundle_ReadsTheExportUnchanged()
    {
        var exportPath = WriteZip("export.zip", ("Releases/Releases-1.json", new { Id = "Releases-1", Version = "1.0.0" }, "Release"));

        var extraction = await Reader().ReadAsync(exportPath);

        extraction.Documents.Select(d => d.SourcePath).ShouldBe(["manifest.json", "Releases/Releases-1.json"], ignoreOrder: true);
    }

    [Fact]
    public async Task MainFileIsTheBundle_ReadWithTheHistoryLimits()
    {
        var writer = new OctopusHistoryBundleWriter();
        foreach (var n in Enumerable.Range(1, 5)) await writer.WriteDocumentAsync(_sessionDirectory, OctopusResourceKind.Release, new OctopusReleaseDto { Id = $"Releases-{n}", Version = $"1.0.{n}" });
        var bundlePath = await writer.BuildBundleAsync(_sessionDirectory, "inst-1");
        var tightExportLimits = new OctopusArchiveExtractionOptions { MaxEntryCount = 2 };

        var extraction = await new OctopusImportSourceReader(new OctopusArchiveExtractor(tightExportLimits), new OctopusInputExtractor()).ReadAsync(bundlePath);

        extraction.Documents.Count.ShouldBe(6);
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("history/../../escape.json")]
    public async Task TraversalInHistoryBundle_Rejected(string hostileEntry)
    {
        var exportPath = WriteZip("export.zip", ("Projects/Projects-1.json", new { Id = "Projects-1" }, "Project"));
        using (var zip = ZipFile.Open(Path.Combine(_sessionDirectory, OctopusImportSourceReader.HistoryBundleFileName), ZipArchiveMode.Create))
        {
            using var entry = new StreamWriter(zip.CreateEntry(hostileEntry).Open());
            entry.Write("{\"Id\":\"Releases-1\"}");
        }

        var ex = await Should.ThrowAsync<OctopusArchiveExtractionException>(() => Reader().ReadAsync(exportPath));

        ex.Code.ShouldBe(OctopusArchiveExtractionErrorCodes.EntryPathUnsafe);
    }

    public void Dispose() => Directory.Delete(_sessionDirectory, recursive: true);

    private static OctopusImportSourceReader Reader() => new(new OctopusArchiveExtractor(), new OctopusInputExtractor());

    private string WriteZip(string fileName, params (string Path, object Document, string DocumentType)[] documents)
    {
        var path = Path.Combine(_sessionDirectory, fileName);
        var contents = documents.Select(d => (d.Path, Bytes: JsonSerializer.SerializeToUtf8Bytes(d.Document), d.DocumentType, Id: JsonDocument.Parse(JsonSerializer.Serialize(d.Document)).RootElement.GetProperty("Id").GetString())).ToList();
        var manifest = new { SchemaVersions = new[] { "Script0700" }, Entries = contents.Select(c => new { c.Id, Name = c.Id, c.DocumentType, ExportType = "FullDocument", DocumentSource = c.Path, Hash = Convert.ToHexString(SHA1.HashData(c.Bytes)).ToLowerInvariant() }) };

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest));
        foreach (var content in contents) Add(zip, content.Path, content.Bytes);

        return path;
    }

    private static void Add(ZipArchive zip, string path, byte[] bytes)
    {
        using var stream = zip.CreateEntry(path).Open();
        stream.Write(bytes);
    }
}
