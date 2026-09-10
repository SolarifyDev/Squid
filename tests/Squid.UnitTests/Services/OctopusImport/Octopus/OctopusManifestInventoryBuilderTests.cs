using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Octopus;

public class OctopusManifestInventoryBuilderTests
{
    private readonly OctopusInputExtractor _inputExtractor = new();
    private readonly OctopusManifestInventoryBuilder _builder = new();

    [Fact]
    public async Task Build_WithManifest_MatchesDocumentsAndCountsKinds()
    {
        var projectJson = """{"Id":"Projects-1","Name":"Project"}""";
        var environmentJson = """{"Id":"Environments-1","Name":"Production"}""";
        var result = await ExtractEntriesAsync(
            ("manifest.json", BuildManifestJson(
                ("Projects-1", "Project", "Projects-1.json", Sha1(projectJson)),
                ("Environments-1", "StaticDeploymentEnvironment", "Environments-1.json", Sha1(environmentJson)))),
            ("Projects-1.json", projectJson),
            ("Environments-1.json", environmentJson));

        var inventory = _builder.Build(result);

        inventory.HasManifest.ShouldBeTrue();
        inventory.Items.Count.ShouldBe(2);
        inventory.Items.All(i => i.HasDocument).ShouldBeTrue();
        inventory.Items.All(i => i.HashMatches).ShouldBeTrue();
        inventory.Counts.Single(c => c.Kind == OctopusDocumentKind.Project).Count.ShouldBe(1);
        inventory.Counts.Single(c => c.Kind == OctopusDocumentKind.Environment).Count.ShouldBe(1);
        inventory.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task Build_MissingManifest_ReturnsBlocker()
    {
        var result = await ExtractEntriesAsync(("Projects-1.json", """{"Id":"Projects-1"}"""));

        var inventory = _builder.Build(result);

        inventory.HasManifest.ShouldBeFalse();
        inventory.Diagnostics.Single().Code.ShouldBe(OctopusInputExtractionDiagnosticCodes.ManifestMissing);
    }

    [Fact]
    public async Task Build_MissingManifestDocument_ReturnsBlockerWithoutSourceValues()
    {
        var result = await ExtractEntriesAsync(
            ("manifest.json", BuildManifestJson(
                ("Projects-1", "Project", "Projects-1.json", "0123456789012345678901234567890123456789"))));

        var inventory = _builder.Build(result);

        var diagnostic = inventory.Diagnostics.Single(d => d.Code == OctopusInputExtractionDiagnosticCodes.ManifestDocumentMissing);
        diagnostic.Severity.ShouldBe(OctopusImportCompatibilitySeverity.Blocker);
        diagnostic.SourcePath.ShouldBe("Projects-1.json");
        diagnostic.SourceId.ShouldBe("Projects-1");
        diagnostic.Message.ShouldNotContain("0123456789012345678901234567890123456789");
    }

    [Fact]
    public async Task Build_HashMismatch_ReturnsBlockerWithoutJsonContentOrHashValues()
    {
        var secretValue = "super-secret-source-value";
        var projectJson = $$"""{"Id":"Projects-1","Name":"Project","Value":"{{secretValue}}"}""";
        var result = await ExtractEntriesAsync(
            ("manifest.json", BuildManifestJson(
                ("Projects-1", "Project", "Projects-1.json", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"))),
            ("Projects-1.json", projectJson));

        var inventory = _builder.Build(result);

        var diagnostic = inventory.Diagnostics.Single(d => d.Code == OctopusInputExtractionDiagnosticCodes.ManifestHashMismatch);
        diagnostic.Message.ShouldContain("Projects-1.json");
        diagnostic.Message.ShouldNotContain(secretValue);
        diagnostic.Message.ShouldNotContain("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        diagnostic.Message.ShouldNotContain(Sha1(projectJson));
    }

    [Fact]
    public async Task Build_SourceIdMismatch_ReturnsBlocker()
    {
        var projectJson = """{"Id":"Projects-2","Name":"Project"}""";
        var result = await ExtractEntriesAsync(
            ("manifest.json", BuildManifestJson(
                ("Projects-1", "Project", "Projects-1.json", Sha1(projectJson)))),
            ("Projects-1.json", projectJson));

        var inventory = _builder.Build(result);

        var diagnostic = inventory.Diagnostics.Single(d => d.Code == OctopusInputExtractionDiagnosticCodes.ManifestSourceIdMismatch);
        diagnostic.SourceId.ShouldBe("Projects-1");
        diagnostic.SourcePath.ShouldBe("Projects-1.json");
    }

    [Fact]
    public async Task Build_DocumentTypeMismatch_ReturnsWarning()
    {
        var projectJson = """{"Id":"Projects-1","Name":"Project"}""";
        var result = await ExtractEntriesAsync(
            ("manifest.json", BuildManifestJson(
                ("Projects-1", "Lifecycle", "Projects-1.json", Sha1(projectJson)))),
            ("Projects-1.json", projectJson));

        var inventory = _builder.Build(result);

        var diagnostic = inventory.Diagnostics.Single(d => d.Code == OctopusInputExtractionDiagnosticCodes.ManifestDocumentTypeMismatch);
        diagnostic.Severity.ShouldBe(OctopusImportCompatibilitySeverity.Warning);
        diagnostic.DocumentKind.ShouldBe(OctopusDocumentKind.Lifecycle);
    }

    [Fact]
    public async Task Build_DuplicateManifestSource_ReturnsBlocker()
    {
        var projectJson = """{"Id":"Projects-1","Name":"Project"}""";
        var result = await ExtractEntriesAsync(
            ("manifest.json", BuildManifestJson(
                ("Projects-1", "Project", "Projects-1.json", Sha1(projectJson)),
                ("Projects-1", "Project", "Projects-1.json", Sha1(projectJson)))),
            ("Projects-1.json", projectJson));

        var inventory = _builder.Build(result);

        inventory.Diagnostics.Any(d => d.Code == OctopusInputExtractionDiagnosticCodes.ManifestDuplicateSource).ShouldBeTrue();
    }

    [Fact]
    public async Task Build_UnlistedDocument_ReturnsWarning()
    {
        var projectJson = """{"Id":"Projects-1","Name":"Project"}""";
        var environmentJson = """{"Id":"Environments-1","Name":"Production"}""";
        var result = await ExtractEntriesAsync(
            ("manifest.json", BuildManifestJson(
                ("Projects-1", "Project", "Projects-1.json", Sha1(projectJson)))),
            ("Projects-1.json", projectJson),
            ("Environments-1.json", environmentJson));

        var inventory = _builder.Build(result);

        var diagnostic = inventory.Diagnostics.Single(d => d.Code == OctopusInputExtractionDiagnosticCodes.DocumentNotInManifest);
        diagnostic.Severity.ShouldBe(OctopusImportCompatibilitySeverity.Warning);
        diagnostic.SourcePath.ShouldBe("Environments-1.json");
        diagnostic.SourceId.ShouldBe("Environments-1");
    }

    [Fact]
    public async Task Build_ManifestListedWorkerPool_DoesNotReportMissingDocument()
    {
        var workerPoolJson = """{"Id":"WorkerPools-1","Name":"Default Worker Pool","DocumentType":"WorkerPool"}""";
        var result = await ExtractEntriesAsync(
            ("manifest.json", BuildManifestJson(
                ("WorkerPools-1", "WorkerPool", "WorkerPools-1.json", Sha1(workerPoolJson)))),
            ("WorkerPools-1.json", workerPoolJson));

        var inventory = _builder.Build(result);

        inventory.Diagnostics.ShouldNotContain(d => d.Code == OctopusInputExtractionDiagnosticCodes.ManifestDocumentMissing);
        inventory.Diagnostics.ShouldNotContain(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);
        inventory.Items.Single().HasDocument.ShouldBeTrue();
        inventory.Items.Single().Classification.Kind.ShouldBe(OctopusDocumentKind.WorkerPool);
        inventory.Counts.Single(c => c.Kind == OctopusDocumentKind.WorkerPool).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Build_ManifestListedUnknownDocument_DoesNotReportMissingDocumentWhenJsonExists()
    {
        var extensionJson = """{"Id":"PluginResources-1","Name":"Extension owned resource","DocumentType":"PluginResource"}""";
        var result = await ExtractEntriesAsync(
            ("manifest.json", BuildManifestJson(
                ("PluginResources-1", "PluginResource", "PluginResources-1.json", Sha1(extensionJson)))),
            ("PluginResources-1.json", extensionJson));

        var inventory = _builder.Build(result);

        inventory.Diagnostics.ShouldNotContain(d => d.Code == OctopusInputExtractionDiagnosticCodes.ManifestDocumentMissing);
        inventory.Items.Single().HasDocument.ShouldBeTrue();
        inventory.Items.Single().Classification.Kind.ShouldBe(OctopusDocumentKind.Unknown);
        inventory.Diagnostics.ShouldContain(d =>
            d.Code == OctopusInputExtractionDiagnosticCodes.UnrecognizedDocument &&
            d.Severity == OctopusImportCompatibilitySeverity.Warning);
    }

    [Fact]
    public async Task Build_ManifestSnapshotsAndHistory_AreCountedByClassification()
    {
        var snapshotJson = """{"Id":"variableset-Projects-1-s-3-ABC","OwnerId":"Projects-1"}""";
        var releaseJson = """{"Id":"Releases-1","ProjectId":"Projects-1"}""";
        var result = await ExtractEntriesAsync(
            ("manifest.json", BuildManifestJson(
                ("variableset-Projects-1-s-3-ABC", "ProjectVariables", "variableset-Projects-1-s-3-ABC.json", Sha1(snapshotJson)),
                ("Releases-1", "Release", "Releases-1.json", Sha1(releaseJson)))),
            ("variableset-Projects-1-s-3-ABC.json", snapshotJson),
            ("Releases-1.json", releaseJson));

        var inventory = _builder.Build(result);

        inventory.Counts.Single(c => c.Kind == OctopusDocumentKind.VariableSetSnapshot).Count.ShouldBe(1);
        inventory.Counts.Single(c => c.Kind == OctopusDocumentKind.Release).Count.ShouldBe(1);
        inventory.Items.All(i => i.Classification.IsOutOfScopeHistory).ShouldBeTrue();
    }

    private async Task<OctopusInputExtractionResult> ExtractEntriesAsync(params (string Path, string Json)[] entries)
    {
        var archiveEntries = entries
            .Select(e => new OctopusExtractedArchiveEntry(e.Path, Encoding.UTF8.GetBytes(e.Json)))
            .ToList();

        return await _inputExtractor.ExtractJsonEntriesAsync(archiveEntries);
    }

    private static string BuildManifestJson(params (string Id, string DocumentType, string DocumentSource, string Hash)[] entries)
    {
        var manifest = new
        {
            SchemaVersions = Array.Empty<string>(),
            Entries = entries.Select(e => new
            {
                e.Id,
                Name = e.Id,
                e.DocumentType,
                ExportType = "FullDocument",
                e.DocumentSource,
                ParentId = (string)null,
                e.Hash
            })
        };

        return JsonSerializer.Serialize(manifest);
    }

    private static string Sha1(string value)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
